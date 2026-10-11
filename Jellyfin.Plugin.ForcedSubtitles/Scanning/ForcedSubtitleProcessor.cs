using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
using Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ForcedSubtitles.Scanning;

/// <summary>
/// Walks the selected libraries, works out which videos are missing subtitles,
/// and downloads only what is missing.
/// </summary>
public sealed class ForcedSubtitleProcessor
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleSaver _saver;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly OpenSubtitlesClient _client;
    private readonly SubtitleInspector _inspector;
    private readonly ScanStore _store;
    private readonly ILogger<ForcedSubtitleProcessor> _logger;
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
    /// <param name="logger">Logger.</param>
    public ForcedSubtitleProcessor(
        ILibraryManager libraryManager,
        SubtitleSaver saver,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        OpenSubtitlesClient client,
        SubtitleInspector inspector,
        ScanStore store,
        ILogger<ForcedSubtitleProcessor> logger)
    {
        _libraryManager = libraryManager;
        _saver = saver;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _client = client;
        _inspector = inspector;
        _store = store;
        _logger = logger;
    }

    private enum Need
    {
        Nothing,
        Forced,
        Full
    }

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
            _logger.LogWarning("Forced Subtitles: no OpenSubtitles API key configured, skipping");
            return;
        }

        // A scheduled run and a post-scan run shouldn't overlap and double-download.
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Forced Subtitles: a scan is already running, skipping");
            return;
        }

        try
        {
            // Check the login once up front; otherwise bad credentials turn every video into an error.
            try
            {
                await _client.EnsureLoggedInAsync(false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Forced Subtitles: cannot log in to OpenSubtitles, skipping run: {Message}", ex.Message);
                return;
            }

            var videos = GetVideos(config);
            _logger.LogInformation("Forced Subtitles: checking {Count} videos (new only: {NewOnly})", videos.Count, newItemsOnly);

            int downloads = 0, searched = 0;
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

                var outcome = await ProcessAsync(video, record, config, cancellationToken).ConfigureAwait(false);
                if (outcome is null)
                {
                    continue;
                }

                searched++;
                _store.Set(video.Id, outcome);
                if (outcome.Status == ScanStatus.Downloaded)
                {
                    downloads++;
                }

                if (searched % 25 == 0)
                {
                    _store.Save();
                }

                if (downloads >= Math.Max(1, config.MaxDownloadsPerRun))
                {
                    _logger.LogInformation("Forced Subtitles: reached {Max} downloads for this run, stopping", config.MaxDownloadsPerRun);
                    break;
                }

                if (outcome.Detail?.StartsWith("Quota", StringComparison.Ordinal) == true)
                {
                    break;
                }
            }

            _store.Save();
            progress.Report(100);
            _logger.LogInformation("Forced Subtitles: done, {Searched} videos updated, {Downloads} subtitles downloaded", searched, downloads);
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
                _logger.LogError(ex, "Forced Subtitles: background run failed");
            }
        });
    }

    /// <summary>
    /// Checks one video. Returns null when nothing changed and no record needs writing.
    /// </summary>
    private async Task<ScanRecord?> ProcessAsync(Video video, ScanRecord? previous, PluginConfiguration config, CancellationToken cancellationToken)
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

        // We already saved a file; Jellyfin just may not have picked it up yet.
        if (previous?.Status == ScanStatus.Downloaded)
        {
            return null;
        }

        if (previous?.Status == ScanStatus.NotFound
            && previous.LastChecked > DateTime.UtcNow.AddDays(-Math.Max(1, config.RecheckAfterDays)))
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

            var (content, remaining) = await _client.DownloadAsync(best.File.FileId, cancellationToken).ConfigureAwait(false);
            await using (content.ConfigureAwait(false))
            {
                await _saver.SaveAsync(
                    video,
                    content,
                    _inspector.ToThreeLetter(query.Language),
                    wantForced,
                    best.Entry.Attributes.HearingImpaired,
                    config.SaveLocation,
                    cancellationToken).ConfigureAwait(false);
            }

            _providerManager.QueueRefresh(
                video.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
                RefreshPriority.Normal);

            _logger.LogInformation(
                "Forced Subtitles: downloaded {Kind} {Language} subtitle for {Name} ({Release}{Hash}); {Remaining} downloads left today",
                wantForced ? "forced" : "full",
                language,
                video.Name,
                best.Entry.Attributes.Release,
                best.Entry.Attributes.MovieHashMatch == true ? ", hash match" : string.Empty,
                remaining);

            return Record(video, ScanStatus.Downloaded, (wantForced ? "Forced: " : "Full: ") + best.Entry.Attributes.Release);
        }
        catch (DownloadQuotaExceededException ex)
        {
            _logger.LogWarning("Forced Subtitles: {Message}", ex.Message);
            return Record(video, ScanStatus.Error, "Quota reached, will retry next run");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Forced Subtitles: failed for {Name}", video.Name);
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
                _logger.LogWarning("Forced Subtitles: none of the selected libraries exist any more; re-pick them in the plugin settings");
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
