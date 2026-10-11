using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;

/// <summary>
/// Minimal client for the OpenSubtitles.com REST API (https://opensubtitles.stoplight.io).
/// </summary>
public sealed class OpenSubtitlesClient : IDisposable
{
    private const string DefaultHost = "api.opensubtitles.com";
    private static readonly string UserAgent = "JellyfinForcedSubtitles v"
        + (typeof(OpenSubtitlesClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    // The API allows 5 requests/second per IP; stay comfortably below it.
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan LoginFailureBackoff = TimeSpan.FromMinutes(15);

    // Used when the API says the allowance is gone but not when it comes back.
    private static readonly TimeSpan QuotaFallbackWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenSubtitlesClient> _logger;
    private readonly CredentialsProvider _credentials;
    private readonly string _blockFile;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private DateTime _lastRequest = DateTime.MinValue;
    private string? _token;
    private string _host = DefaultHost;
    private DateTime _tokenExpires = DateTime.MinValue;
    private string? _tokenOwner;
    private string? _failedOwner;
    private DateTime _loginRetryAfter = DateTime.MinValue;
    private string? _lastLoginError;
    private DateTime _downloadsBlockedUntil = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSubtitlesClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="credentials">Credentials provider.</param>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public OpenSubtitlesClient(IHttpClientFactory httpClientFactory, CredentialsProvider credentials, IApplicationPaths paths, ILogger<OpenSubtitlesClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _credentials = credentials;

        // Remember a used-up allowance across restarts, so a restart doesn't spend a request finding out again.
        _blockFile = Path.Combine(paths.PluginConfigurationsPath, "ForcedSubtitles", "download-limit.txt");
        _downloadsBlockedUntil = ReadBlockedUntil(_blockFile) ?? DateTime.MinValue;
    }

    /// <summary>
    /// Raised when the daily download allowance runs out (not again while it stays used up),
    /// with the time (UTC) it renews.
    /// </summary>
    public event EventHandler<DateTime>? DownloadsBlocked;

    /// <summary>Gets the credentials currently in use.</summary>
    public OpenSubtitlesCredentials Credentials => _credentials.Get();

    /// <summary>Gets a value indicating whether an API key has been configured.</summary>
    public bool IsConfigured => Credentials.ApiKey.Length > 0;

    /// <summary>
    /// Gets the time (UTC) the daily download allowance renews, while it is used up; otherwise null.
    /// </summary>
    public DateTime? DownloadsBlockedUntil
    {
        get
        {
            var until = _downloadsBlockedUntil;
            return until > DateTime.UtcNow ? until : null;
        }
    }

    /// <summary>Searches for subtitles.</summary>
    /// <param name="query">The search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching subtitles (first page, up to 60).</returns>
    public async Task<IReadOnlyList<SubtitleEntry>> SearchAsync(SubtitleQuery query, CancellationToken cancellationToken)
    {
        var url = "/subtitles?" + BuildQueryString(query);

        // Searching works without login, but a token routes VIP users to their faster host.
        var authenticated = await TryEnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), authenticated, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var result = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken).ConfigureAwait(false);
        return result?.Data ?? [];
    }

    /// <summary>Downloads a subtitle file. Counts against the account's daily allowance.</summary>
    /// <param name="fileId">The OpenSubtitles file id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subtitle content in SRT format and the remaining daily downloads, if the API reported them.</returns>
    public async Task<(Stream Content, int? Remaining)> DownloadAsync(int fileId, CancellationToken cancellationToken)
    {
        // Don't spend a request on a download we already know will be refused.
        if (DownloadsBlockedUntil is DateTime blocked)
        {
            throw new DownloadQuotaExceededException(
                $"OpenSubtitles daily download limit reached; renews at {blocked:yyyy-MM-dd HH:mm} UTC");
        }

        await EnsureLoggedInAsync(false, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/download")
            {
                Content = JsonContent.Create(new { file_id = fileId, sub_format = "srt" })
            },
            true,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotAcceptable)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            DownloadResponse? refused = null;
            try
            {
                refused = JsonSerializer.Deserialize<DownloadResponse>(body);
            }
            catch (JsonException)
            {
            }

            BlockDownloads(refused?.ResetTimeUtc);
            throw new DownloadQuotaExceededException("OpenSubtitles daily download limit reached: " + (refused?.Message ?? body));
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var info = await response.Content.ReadFromJsonAsync<DownloadResponse>(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(info?.Link))
        {
            throw new HttpRequestException("OpenSubtitles did not return a download link: " + info?.Message);
        }

        // That was the last one for today: stop before the next search rather than after a refused download.
        if (info.Remaining is <= 0)
        {
            BlockDownloads(info.ResetTimeUtc);
        }

        var http = _httpClientFactory.CreateClient();
        var bytes = await http.GetByteArrayAsync(info.Link, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Downloaded OpenSubtitles file {FileId}; {Remaining} downloads left today", fileId, info.Remaining);
        return (new MemoryStream(bytes), info.Remaining);
    }

    /// <summary>Logs in and returns the account's download allowance. Used by "Test connection".</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Account info.</returns>
    public async Task<UserInfo> GetUserInfoAsync(CancellationToken cancellationToken)
    {
        // Force a fresh login so changed credentials are actually tested.
        await EnsureLoggedInAsync(true, cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "/infos/user"), true, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<UserInfoResponse>(cancellationToken).ConfigureAwait(false);
        return result?.Data ?? new UserInfo();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _requestLock.Dispose();
        _loginLock.Dispose();
    }

    /// <summary>
    /// Builds the query string. OpenSubtitles redirects unless parameters are sorted and lowercase.
    /// </summary>
    internal static string BuildQueryString(SubtitleQuery q)
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = q.Language.ToLowerInvariant(),
            ["foreign_parts_only"] = q.ForeignParts.ToString().ToLowerInvariant(),
        };

        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        if (q.IsEpisode)
        {
            p["type"] = "episode";
            if (q.ImdbId is int imdb)
            {
                p["imdb_id"] = I(imdb);
            }
            else if (q.ParentImdbId is int parentImdb)
            {
                p["parent_imdb_id"] = I(parentImdb);
            }
            else if (q.ParentTmdbId is int parentTmdb)
            {
                p["parent_tmdb_id"] = I(parentTmdb);
            }
            else if (!string.IsNullOrWhiteSpace(q.Text))
            {
                p["query"] = q.Text.ToLowerInvariant();
            }

            if (q.ImdbId is null)
            {
                if (q.SeasonNumber is int season)
                {
                    p["season_number"] = I(season);
                }

                if (q.EpisodeNumber is int episode)
                {
                    p["episode_number"] = I(episode);
                }
            }
        }
        else
        {
            p["type"] = "movie";
            if (q.ImdbId is int imdb)
            {
                p["imdb_id"] = I(imdb);
            }
            else if (q.TmdbId is int tmdb)
            {
                p["tmdb_id"] = I(tmdb);
            }
            else if (!string.IsNullOrWhiteSpace(q.Text))
            {
                p["query"] = q.Text.ToLowerInvariant();
                if (q.Year is int year)
                {
                    p["year"] = I(year);
                }
            }
        }

        if (!string.IsNullOrEmpty(q.MovieHash))
        {
            p["moviehash"] = q.MovieHash.ToLowerInvariant();
        }

        return string.Join('&', p.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>
    /// How long to wait after a 429: the server's Retry-After if it sent one (capped), else a short backoff.
    /// </summary>
    internal static TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int attempt, DateTimeOffset now)
    {
        var fallback = TimeSpan.FromSeconds(1 + attempt);
        var delay = retryAfter?.Delta ?? (retryAfter?.Date is DateTimeOffset date ? date - now : fallback);
        if (delay <= TimeSpan.Zero)
        {
            return fallback;
        }

        return delay > MaxRetryAfter ? MaxRetryAfter : delay;
    }

    /// <summary>
    /// When downloads come back: the API's reset time (always UTC, even if it arrives without a zone), else a short wait.
    /// </summary>
    internal static DateTime GetDownloadsBlockedUntil(DateTime? resetTimeUtc, DateTime now)
    {
        if (resetTimeUtc is DateTime reset)
        {
            var utc = reset.Kind switch
            {
                DateTimeKind.Utc => reset,
                DateTimeKind.Local => reset.ToUniversalTime(),
                _ => DateTime.SpecifyKind(reset, DateTimeKind.Utc)
            };
            if (utc > now)
            {
                return utc;
            }
        }

        return now + QuotaFallbackWait;
    }

    /// <summary>Reads a saved "blocked until" time; null if there is none or it can't be read.</summary>
    internal static DateTime? ReadBlockedUntil(string path)
    {
        try
        {
            return File.Exists(path)
                && DateTime.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until)
                && until.Kind == DateTimeKind.Utc
                    ? until
                    : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves a "blocked until" time (UTC).</summary>
    internal static void WriteBlockedUntil(string path, DateTime until)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, until.ToString("o", CultureInfo.InvariantCulture));
    }

    private void BlockDownloads(DateTime? resetTimeUtc)
    {
        var wasBlocked = DownloadsBlockedUntil is not null;
        var until = GetDownloadsBlockedUntil(resetTimeUtc, DateTime.UtcNow);
        _downloadsBlockedUntil = until;
        _logger.LogInformation("OpenSubtitles download allowance used up; pausing downloads until {Until:yyyy-MM-dd HH:mm} UTC", until);

        try
        {
            WriteBlockedUntil(_blockFile, until);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the download limit to {Path}", _blockFile);
        }

        if (!wasBlocked)
        {
            // A listener's failure mustn't turn a successful download into an error.
            try
            {
                DownloadsBlocked?.Invoke(this, until);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Download limit listener failed");
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 300)
        {
            body = body[..300];
        }

        throw new HttpRequestException($"OpenSubtitles returned {(int)response.StatusCode}: {body}", null, response.StatusCode);
    }

    /// <summary>Logs in if needed. Throws with a readable message if that isn't possible.</summary>
    /// <param name="force">Ignore the cached token and any recent failure (used by "Test connection").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task EnsureLoggedInAsync(bool force, CancellationToken cancellationToken)
    {
        var config = Credentials;
        if (!config.HasLogin)
        {
            throw new InvalidOperationException("No OpenSubtitles username and password. Enter them on the plugin settings page.");
        }

        var owner = config.ApiKey + "|" + config.Username + "|" + config.Password;
        if (!force && _token is not null && _tokenOwner == owner && DateTime.UtcNow < _tokenExpires)
        {
            return;
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _token is not null && _tokenOwner == owner && DateTime.UtcNow < _tokenExpires)
            {
                return;
            }

            // Don't hammer the login endpoint (and risk a block) with credentials that just failed.
            if (!force && _failedOwner == owner && DateTime.UtcNow < _loginRetryAfter)
            {
                throw new HttpRequestException("OpenSubtitles login failed recently: " + _lastLoginError);
            }

            _token = null;
            _host = DefaultHost;
            try
            {
                using var response = await SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Post, "/login")
                    {
                        Content = JsonContent.Create(new { username = config.Username, password = config.Password })
                    },
                    false,
                    cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

                var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(login?.Token))
                {
                    throw new HttpRequestException("OpenSubtitles login returned no token.");
                }

                _token = login.Token;
                _host = string.IsNullOrWhiteSpace(login.BaseUrl) ? DefaultHost : login.BaseUrl.Trim().TrimEnd('/');
                _tokenOwner = owner;
                _tokenExpires = DateTime.UtcNow + TokenLifetime;
                _failedOwner = null;
                _logger.LogInformation("Logged in to OpenSubtitles as {User} ({Host})", config.Username, _host);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failedOwner = owner;
                _loginRetryAfter = DateTime.UtcNow + LoginFailureBackoff;
                _lastLoginError = ex.Message;
                _logger.LogWarning("OpenSubtitles login failed for {User}: {Message}", config.Username, ex.Message);
                throw;
            }
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>Logs in if credentials exist; returns false instead of throwing.</summary>
    private async Task<bool> TryEnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (!Credentials.HasLogin)
        {
            return false;
        }

        try
        {
            await EnsureLoggedInAsync(false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a request with throttling, retrying on 429 and re-authenticating once on 401.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, bool authenticated, CancellationToken cancellationToken)
    {
        var config = Credentials;
        if (config.ApiKey.Length == 0)
        {
            throw new InvalidOperationException("No OpenSubtitles API key: enter one in the plugin settings or build the plugin with a built-in key.");
        }

        var http = _httpClientFactory.CreateClient();
        var reauthenticated = false;

        for (int attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            var host = _host.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? _host : "https://" + _host;
            request.RequestUri = new Uri(host + "/api/v1" + request.RequestUri!.OriginalString);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Add("Api-Key", config.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (authenticated && _token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }

            HttpResponseMessage response;
            await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _lastRequest + MinRequestInterval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                _lastRequest = DateTime.UtcNow;
            }
            finally
            {
                _requestLock.Release();
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var delay = GetRetryDelay(response.Headers.RetryAfter, attempt, DateTimeOffset.UtcNow);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated && !reauthenticated)
            {
                response.Dispose();
                reauthenticated = true;
                _token = null;
                if (!await TryEnsureLoggedInAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new HttpRequestException("OpenSubtitles rejected the credentials.", null, HttpStatusCode.Unauthorized);
                }

                continue;
            }

            return response;
        }
    }
}
