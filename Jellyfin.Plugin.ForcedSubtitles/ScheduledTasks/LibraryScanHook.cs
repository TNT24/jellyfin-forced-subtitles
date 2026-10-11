using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ForcedSubtitles.Scanning;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ForcedSubtitles.ScheduledTasks;

/// <summary>
/// Runs after every library scan and checks only videos that haven't been checked before,
/// so newly added movies and episodes get forced subtitles straight away.
/// </summary>
public class LibraryScanHook : ILibraryPostScanTask
{
    private readonly ForcedSubtitleProcessor _processor;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScanHook"/> class.
    /// </summary>
    /// <param name="processor">The processor.</param>
    public LibraryScanHook(ForcedSubtitleProcessor processor)
    {
        _processor = processor;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.CheckOnLibraryScan != true)
        {
            return Task.CompletedTask;
        }

        // Run in the background so a slow API (or a first run over a big library) doesn't hold up the scan.
        _processor.RunInBackground(true);
        return Task.CompletedTask;
    }
}
