using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ForcedSubtitles.Notifications;
using Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JellyfinActivity = Jellyfin.Database.Implementations.Entities.ActivityLog;

namespace Jellyfin.Plugin.ForcedSubtitles.Scanning;

/// <summary>
/// Watches the OpenSubtitles daily download allowance: adds an entry to Jellyfin's activity log
/// (Dashboard → Activity) when it runs out, and starts a check once it renews instead of waiting
/// for the next scheduled run.
/// </summary>
/// <remarks>
/// Runs inside Jellyfin's startup, so nothing here may throw: every failure is logged and skipped.
/// </remarks>
public sealed class DownloadLimitMonitor : IHostedService, IDisposable
{
    // Give OpenSubtitles a moment past the reset time before trying again.
    private static readonly TimeSpan ResumeDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromDays(2);

    private readonly IServiceProvider _services;
    private readonly OpenSubtitlesClient _client;
    private readonly ActivityLog _activity;
    private readonly ILogger<DownloadLimitMonitor> _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _pending;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadLimitMonitor"/> class.
    /// </summary>
    /// <param name="services">Service provider, for Jellyfin services looked up only when needed.</param>
    /// <param name="client">OpenSubtitles client.</param>
    /// <param name="activity">The plugin's activity log.</param>
    /// <param name="logger">Logger.</param>
    public DownloadLimitMonitor(IServiceProvider services, OpenSubtitlesClient client, ActivityLog activity, ILogger<DownloadLimitMonitor> logger)
    {
        _services = services;
        _client = client;
        _activity = activity;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _client.DownloadsBlocked += OnDownloadsBlocked;

            // Still used up from before a restart: resume when it renews.
            if (_client.DownloadsBlockedUntil is DateTime until)
            {
                ScheduleResume(until);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Forced Subtitles: could not start the download limit monitor");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _client.DownloadsBlocked -= OnDownloadsBlocked;
            lock (_lock)
            {
                _pending?.Cancel();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forced Subtitles: error stopping the download limit monitor");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    /// <summary>Works out how long to wait before resuming downloads.</summary>
    /// <param name="blockedUntil">When the allowance renews (UTC).</param>
    /// <param name="now">Current time (UTC).</param>
    /// <returns>The wait; zero if it has already renewed.</returns>
    internal static TimeSpan GetResumeWait(DateTime blockedUntil, DateTime now)
    {
        // The allowance renews daily, so a longer wait means a bad time; the scheduled task covers that case anyway.
        var wait = blockedUntil + ResumeDelay - now;
        return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait > MaxWait ? MaxWait : wait;
    }

    private void OnDownloadsBlocked(object? sender, DateTime until)
    {
        Notify(
            "Forced Subtitles: OpenSubtitles daily download limit reached",
            $"Downloads resume automatically after {ForcedSubtitleProcessor.FormatUtc(until)}.",
            LogLevel.Warning);

        try
        {
            _services.GetService<SubtitleNotifier>()?.LimitReached(until);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forced Subtitles: could not tell Discord about the download limit");
        }

        ScheduleResume(until);
    }

    private void ScheduleResume(DateTime until)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            // Only the latest block matters. The old token source isn't disposed here as its wait may still be using it.
            _pending?.Cancel();
            _pending = cts = new CancellationTokenSource();
        }

        _ = ResumeAfterAsync(until, cts.Token);
    }

    private async Task ResumeAfterAsync(DateTime until, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(GetResumeWait(until, DateTime.UtcNow), cancellationToken).ConfigureAwait(false);

            // Used up again in the meantime: that block scheduled its own resume.
            if (_client.DownloadsBlockedUntil is not null)
            {
                return;
            }

            _activity.Info("Daily download limit renewed; checking for missing subtitles");
            Notify(
                "Forced Subtitles: OpenSubtitles downloads resumed",
                "The daily download limit has renewed. Checking for missing subtitles now.",
                LogLevel.Information);

            // Resolved here rather than injected, to keep the monitor's construction as simple as possible.
            _services.GetRequiredService<ForcedSubtitleProcessor>().RunInBackground(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _activity.Error("Could not resume downloads automatically; the next scheduled run will carry on", ex);
        }
    }

    /// <summary>Adds an entry to Jellyfin's activity log, in the background. Failures are only logged.</summary>
    private void Notify(string name, string overview, LogLevel severity)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var manager = _services.GetService<IActivityManager>();
                if (manager is null)
                {
                    return;
                }

                await manager.CreateAsync(new JellyfinActivity(name, "ForcedSubtitles", Guid.Empty)
                {
                    ShortOverview = overview,
                    LogSeverity = severity
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forced Subtitles: could not add to Jellyfin's activity log");
            }
        });
    }
}
