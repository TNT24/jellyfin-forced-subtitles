using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ForcedSubtitles.Scanning;

/// <summary>
/// Writes downloaded subtitles where Jellyfin will find them: either Jellyfin's internal
/// metadata folder (media folders stay untouched) or next to the video.
/// </summary>
public sealed class SubtitleSaver
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<SubtitleSaver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSaver"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="libraryMonitor">Library monitor, told about our writes so they don't trigger a rescan.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleSaver(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, ILogger<SubtitleSaver> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <summary>Saves a subtitle for a video.</summary>
    /// <param name="video">The video.</param>
    /// <param name="content">SRT content.</param>
    /// <param name="language">Three-letter language code.</param>
    /// <param name="forced">Whether it is a forced subtitle.</param>
    /// <param name="hearingImpaired">Whether it is SDH.</param>
    /// <param name="location">Where to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path written.</returns>
    public async Task<string> SaveAsync(Video video, Stream content, string language, bool forced, bool hearingImpaired, SubtitleLocation location, CancellationToken cancellationToken)
    {
        var useMediaFolder = location switch
        {
            SubtitleLocation.MediaFolder => true,
            SubtitleLocation.MetadataFolder => false,
            _ => _libraryManager.GetLibraryOptions(video).SaveSubtitlesWithMedia
        };
        var folder = useMediaFolder ? video.ContainingFolderPath : video.GetInternalMetadataPath();

        // Jellyfin matches external subtitles to a video by this exact prefix: <video name>.<lang>[.forced][.sdh]
        var baseName = Path.GetFileNameWithoutExtension(video.Path) + "." + language.ToLowerInvariant()
            + (forced ? ".forced" : string.Empty)
            + (hearingImpaired ? ".sdh" : string.Empty);

        var path = Path.Combine(folder, baseName + ".srt");
        for (int i = 1; File.Exists(path); i++)
        {
            path = Path.Combine(folder, string.Format(CultureInfo.InvariantCulture, "{0}.{1}.srt", baseName, i));
        }

        Directory.CreateDirectory(folder);
        _libraryMonitor.ReportFileSystemChangeBeginning(path);
        try
        {
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (file.ConfigureAwait(false))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(path, false);
        }

        _logger.LogInformation("Saved subtitle to {Path}", path);
        return path;
    }
}
