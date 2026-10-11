using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ForcedSubtitles.Scanning;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ForcedSubtitles.ScheduledTasks;

/// <summary>
/// Scheduled task: checks every video in the selected libraries and downloads missing forced subtitles.
/// Appears under Dashboard → Scheduled Tasks → Subtitles.
/// </summary>
public class ForcedSubtitleTask : IScheduledTask
{
    private readonly ForcedSubtitleProcessor _processor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForcedSubtitleTask"/> class.
    /// </summary>
    /// <param name="processor">The processor.</param>
    public ForcedSubtitleTask(ForcedSubtitleProcessor processor)
    {
        _processor = processor;
    }

    /// <inheritdoc />
    public string Name => "Download missing forced subtitles";

    /// <inheritdoc />
    public string Key => "ForcedSubtitlesDownload";

    /// <inheritdoc />
    public string Description => "Finds videos without forced subtitles and downloads them from OpenSubtitles.com when one exists.";

    /// <inheritdoc />
    public string Category => "Subtitles";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _processor.RunAsync(false, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily at 3am; the OpenSubtitles download allowance resets every 24 hours.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}
