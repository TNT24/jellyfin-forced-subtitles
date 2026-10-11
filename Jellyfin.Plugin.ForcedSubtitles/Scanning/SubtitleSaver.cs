using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ForcedSubtitles.Scanning;

/// <summary>
/// Writes downloaded subtitles where Jellyfin will find them: either Jellyfin's internal
/// metadata folder (media folders stay untouched) or next to the video.
/// </summary>
public sealed class SubtitleSaver
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ActivityLog _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSaver"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="libraryMonitor">Library monitor, told about our writes so they don't trigger a rescan.</param>
    /// <param name="activity">Activity log.</param>
    public SubtitleSaver(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, ActivityLog activity)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _activity = activity;
    }

    /// <summary>
    /// Picks the folder to save into. Called before downloading, because a download counts against
    /// the daily allowance even if the file can't be written afterwards: a read-only media folder
    /// (e.g. a Docker ":ro" mount) falls back to Jellyfin's metadata folder.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="location">Where the user wants subtitles saved.</param>
    /// <returns>The folder to pass to <see cref="SaveAsync"/>.</returns>
    public string ResolveFolder(Video video, SubtitleLocation location)
    {
        var useMediaFolder = location switch
        {
            SubtitleLocation.MediaFolder => true,
            SubtitleLocation.MetadataFolder => false,
            _ => _libraryManager.GetLibraryOptions(video).SaveSubtitlesWithMedia
        };

        var metadataFolder = video.GetInternalMetadataPath();
        if (!useMediaFolder)
        {
            return metadataFolder;
        }

        var mediaFolder = video.ContainingFolderPath;
        if (CanWrite(mediaFolder))
        {
            return mediaFolder;
        }

        _activity.Warning($"Can't write to {mediaFolder}; saving the subtitle for {video.Name} to Jellyfin's metadata folder instead");
        return metadataFolder;
    }

    /// <summary>Saves a subtitle for a video.</summary>
    /// <param name="video">The video.</param>
    /// <param name="folder">Folder from <see cref="ResolveFolder"/>.</param>
    /// <param name="content">SRT content.</param>
    /// <param name="language">Three-letter language code.</param>
    /// <param name="forced">Whether it is a forced subtitle.</param>
    /// <param name="hearingImpaired">Whether it is SDH.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path written.</returns>
    public async Task<string> SaveAsync(Video video, string folder, Stream content, string language, bool forced, bool hearingImpaired, CancellationToken cancellationToken)
    {
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

        _activity.Info("Saved " + path);
        return path;
    }

    private bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, ".forcedsubtitles-" + Guid.NewGuid().ToString("N") + ".tmp");
        _libraryMonitor.ReportFileSystemChangeBeginning(probe);
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(probe, false);
        }
    }
}
