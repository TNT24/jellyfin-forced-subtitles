using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
using Jellyfin.Plugin.ForcedSubtitles.Notifications;
using Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.ForcedSubtitles.Scanning;

/// <summary>
/// Walks the selected libraries, works out which videos are missing subtitles,
/// and downloads only what is missing.
/// </summary>
public sealed class ForcedSubtitleProcessor
{
    // Failed videos wait this long before another try, so a permanent failure doesn't cost a search every run.
    private static readonly TimeSpan ErrorRetryAfter = TimeSpan.FromDays(1);

    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleSaver _saver;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly OpenSubtitlesClient _client;
    private readonly SubtitleInspector _inspector;
    private readonly ScanStore _store;
    private readonly ActivityLog _activity;
    private readonly SubtitleNotifier _notifier;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ForcedSubtitleProcessor"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="saver">Subtitle saver.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="client">OpenSubtitles client.</param>
    /// <param name="inspector">Subtitle inspector.</param>
    /// <param name="store">Scan state store.</param>
    /// <param name="activity">Activity log.</param>
    /// <param name="notifier">Discord notifications.</param>
    public ForcedSubtitleProcessor(
        ILibraryManager libraryManager,
        SubtitleSaver saver,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        OpenSubtitlesClient client,
        SubtitleInspector inspector,
        ScanStore store,
        ActivityLog activity,
        SubtitleNotifier notifier)
    {
        _libraryManager = libraryManager;
        _saver = saver;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _client = client;
        _inspector = inspector;
        _store = store;
        _activity = activity;
        _notifier = notifier;
    }

    private enum Need
    {
        Nothing,
        Forced,
        Full
    }

    /// <summary>Gets a value indicating whether a run is in progress.</summary>
    public bool IsRunning => _runLock.CurrentCount == 0;

    /// <summary>Checks videos and downloads missing subtitles.</summary>
    /// <param name="newItemsOnly">Only look at videos never checked before (used after library scans).</param>
    /// <param name="progress">Progress reporter (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(bool newItemsOnly, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!_client.IsConfigured)
        {
            _activity.Warning("No OpenSubtitles API key configured; skipping run");
            return;
        }

        // A scheduled run and a post-scan run shouldn't overlap and double-download.
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _activity.Info("A check is already running; skipping this one");
            return;
        }

        try
        {
            // Searching is pointless while nothing can be downloaded; the next run picks up where this left off.
            if (_client.DownloadsBlockedUntil is DateTime blocked)
            {
                _activity.Info($"Daily download limit used up until {FormatUtc(blocked)}; skipping run");
                return;
            }

            // Check the login once up front; otherwise bad credentials turn every video into an error.
            try
            {
                await _client.EnsureLoggedInAsync(false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _activity.Warning("Can't log in to OpenSubtitles; skipping run: " + ex.Message);
                await _notifier.LoginFailedAsync(ex.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!newItemsOnly)
            {
                var removed = _store.RemoveWhereMissing(id => _libraryManager.GetItemById(id) is not null);
                if (removed > 0)
                {
                    _activity.Info($"Forgot {removed} videos that are no longer in Jellyfin");
                }
            }

            var videos = GetVideos(config);
            if (!newItemsOnly)
            {
                _activity.Info($"Checking {videos.Count} videos");
            }

            int downloads = 0, searched = 0, notFound = 0, errors = 0;
            var downloaded = new List<DownloadedSubtitle>();
            for (int i = 0; i < videos.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(100.0 * i / Math.Max(1, videos.Count));

                var video = videos[i];
                var record = _store.Get(video.Id);
                if (newItemsOnly && record is not null)
                {
                    continue;
                }

                var outcome = await ProcessAsync(video, record, config, downloaded, cancellationToken).ConfigureAwait(false);
                if (outcome is null)
                {
                    continue;
                }

                searched++;
                _store.Set(video.Id, outcome);
                switch (outcome.Status)
                {
                    case ScanStatus.Downloaded:
                        downloads++;
                        break;
                    case ScanStatus.NotFound:
                        notFound++;
                        break;
                    case ScanStatus.Error:
                        errors++;
                        break;
                }

                if (searched % 25 == 0)
                {
                    _store.Save();
                }

                if (downloads >= Math.Max(1, config.MaxDownloadsPerRun))
                {
                    _activity.Info($"Reached {config.MaxDownloadsPerRun} downloads for this run; stopping");
                    break;
                }

                // Out of downloads (refused, or the last one used them up): stop instead of searching on.
                if (_client.DownloadsBlockedUntil is DateTime until)
                {
                    _activity.Info($"Daily download limit used up until {FormatUtc(until)}; stopping");
                    break;
                }
            }

            _store.Save();
            progress.Report(100);

            // Post-scan runs usually find nothing new; only mention them when they did something.
            if (!newItemsOnly || searched > 0)
            {
                _activity.Info($"{(newItemsOnly ? "New videos checked" : "Done")}: {downloads} downloaded, {notFound} with none available, {errors} failed");
            }

            await _notifier.DownloadsAsync(downloaded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Starts a run without waiting for it; errors are logged rather than lost.</summary>
    /// <param name="newItemsOnly">Only look at videos never checked before.</param>
    public void RunInBackground(bool newItemsOnly)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(newItemsOnly, new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _activity.Error("Run failed", ex);
            }
        });
    }

    /// <summary>Formats a UTC time for log messages.</summary>
    /// <param name="utc">The time.</param>
    /// <returns>For example "2026-10-11 14:30 UTC".</returns>
    internal static string FormatUtc(DateTime utc)
        => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>Works out whether a video that still needs a subtitle should be searched for now.</summary>
    /// <param name="previous">The last result for the video, if any.</param>
    /// <param name="config">Settings.</param>
    /// <param name="now">Current time (UTC).</param>
    /// <param name="fileExists">Checks whether a file exists.</param>
    /// <returns>True to search.</returns>
    internal static bool ShouldSearch(ScanRecord? previous, PluginConfiguration config, DateTime now, Func<string, bool> fileExists)
    {
        switch (previous?.Status)
        {
            case null:
            case ScanStatus.AlreadyHas:
                return true;

            // We saved a file and Jellyfin just hasn't picked it up yet, unless the file has since been deleted.
            case ScanStatus.Downloaded:
                return previous.SavedPath is not null && !fileExists(previous.SavedPath);

            case ScanStatus.NotFound:
                return previous.LastChecked <= now.AddDays(-Math.Max(1, config.RecheckAfterDays));

            // Out of downloads last time: nothing was wrong with the video, so try again on the next run.
            case ScanStatus.Error when previous.Detail?.StartsWith("Quota", StringComparison.Ordinal) == true:
                return true;

            default:
                return previous.LastChecked <= now - ErrorRetryAfter;
        }
    }

    /// <summary>
    /// Checks one video. Returns null when nothing changed and no record needs writing.
    /// A download is also added to <paramref name="downloaded"/>, for Discord.
    /// </summary>
    private async Task<ScanRecord?> ProcessAsync(Video video, ScanRecord? previous, PluginConfiguration config, List<DownloadedSubtitle> downloaded, CancellationToken cancellationToken)
    {
        var language = string.IsNullOrWhiteSpace(config.ForcedLanguage) ? "eng" : config.ForcedLanguage.Trim();
        var state = _inspector.Inspect(video, language);

        // Not probed yet (e.g. just added): we can't see its embedded tracks, so don't guess. Try again later.
        if (!state.IsAnalyzed)
        {
            return null;
        }

        // Local check first: never hit the API for a video that already has what it needs.
        var need = Need.Nothing;
        if (state.AudioIsTarget && !state.HasForced)
        {
            need = Need.Forced;
        }
        else if (!state.AudioIsTarget && config.DownloadFullWhenAudioForeign && !state.HasFull)
        {
            need = Need.Full;
        }

        if (need == Need.Nothing)
        {
            return previous?.Status == ScanStatus.AlreadyHas ? null : Record(video, ScanStatus.AlreadyHas, Describe(state));
        }

        if (!ShouldSearch(previous, config, DateTime.UtcNow, File.Exists))
        {
            return null;
        }

        var wantForced = need == Need.Forced;
        var query = _inspector.BuildQuery(video, language, wantForced ? ForeignPartsFilter.Only : ForeignPartsFilter.Exclude);
        if (query is null)
        {
            return null;
        }

        try
        {
            var results = await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            // A title-only search can match the wrong film or show, so then only trust exact file-hash matches.
            var best = SubtitleRanker.PickBest(results, query.Language, wantForced, video.Path, config, requireHash: !query.HasStrongId);
            if (best is null)
            {
                return Record(video, ScanStatus.NotFound, (wantForced ? "No forced " : "No full ") + language + " subtitle found");
            }

            // Pick the folder before downloading: a download counts against the allowance even if saving fails.
            var folder = _saver.ResolveFolder(video, config.SaveLocation);

            var (content, remaining) = await _client.DownloadAsync(best.File.FileId, cancellationToken).ConfigureAwait(false);
            string savedPath;
            await using (content.ConfigureAwait(false))
            {
                savedPath = await _saver.SaveAsync(
                    video,
                    folder,
                    content,
                    _inspector.ToThreeLetter(query.Language),
                    wantForced,
                    best.Entry.Attributes.HearingImpaired,
                    cancellationToken).ConfigureAwait(false);
            }

            _providerManager.QueueRefresh(
                video.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
                RefreshPriority.Normal);

            var record = Record(video, ScanStatus.Downloaded, (wantForced ? "Forced: " : "Full: ") + best.Entry.Attributes.Release);
            record.SavedPath = savedPath;
            downloaded.Add(new DownloadedSubtitle(
                video.Id,
                record.Name,
                wantForced,
                language,
                best.Entry.Attributes.MovieHashMatch == true,
                remaining));
            _activity.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Downloaded {0} {1} subtitle for {2} ({3}{4}){5}",
                wantForced ? "forced" : "full",
                language,
                record.Name,
                best.Entry.Attributes.Release,
                best.Entry.Attributes.MovieHashMatch == true ? ", hash match" : string.Empty,
                remaining is int left ? $"; {left} downloads left today" : string.Empty));
            return record;
        }
        catch (DownloadQuotaExceededException ex)
        {
            _activity.Warning(ex.Message);
            return Record(video, ScanStatus.Error, "Quota reached, will retry next run");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activity.Error("Failed for " + Record(video, ScanStatus.Error, string.Empty).Name, ex);
            return Record(video, ScanStatus.Error, ex.Message);
        }
    }

    private List<Video> GetVideos(PluginConfiguration config)
    {
        var types = new List<BaseItemKind>();
        if (config.ScanMovies)
        {
            types.Add(BaseItemKind.Movie);
        }

        if (config.ScanEpisodes)
        {
            types.Add(BaseItemKind.Episode);
        }

        if (types.Count == 0)
        {
            return [];
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = types.ToArray(),
            IsVirtualItem = false,
            Recursive = true,
        };

        var libraryIds = (config.LibraryIds ?? [])
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => !g.Equals(Guid.Empty))
            .ToArray();
        if (libraryIds.Length > 0)
        {
            // Jellyfin throws if any id no longer exists, so drop libraries that were deleted since they were picked.
            var existing = libraryIds.Where(id => _libraryManager.GetItemById(id) is Folder).ToArray();
            if (existing.Length == 0)
            {
                _activity.Warning("None of the selected libraries exist any more; pick them again in the plugin settings");
                return [];
            }

            query.AncestorIds = existing;
        }

        return _libraryManager.GetItemList(query)
            .OfType<Video>()
            .Where(v => v.VideoType == VideoType.VideoFile && !string.IsNullOrEmpty(v.Path) && File.Exists(v.Path))
            .ToList();
    }

    private static string Describe(SubtitleState s)
        => s.AudioIsTarget ? "Has forced subtitle" : $"Audio is {s.AudioLanguage}; has full subtitle";

    private static ScanRecord Record(Video video, ScanStatus status, string detail) => new()
    {
        Name = video is MediaBrowser.Controller.Entities.TV.Episode e
            ? $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00} - {e.Name}"
            : $"{video.Name} ({video.ProductionYear})",
        LastChecked = DateTime.UtcNow,
        Status = status,
        Detail = detail
    };
}
