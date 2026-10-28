using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
using Jellyfin.Plugin.ForcedSubtitles.Scanning;

namespace Jellyfin.Plugin.ForcedSubtitles.Notifications;

/// <summary>
/// Decides what goes to Discord and sends it. Never throws: a failed post is only logged,
/// so Discord being down can't break a run.
/// </summary>
public sealed class SubtitleNotifier
{
    // A login that keeps failing would otherwise be reported after every library scan.
    private static readonly TimeSpan RepeatProblemAfter = TimeSpan.FromHours(24);

    private readonly DiscordNotifier _discord;
    private readonly ActivityLog _activity;
    private readonly object _lock = new();
    private string? _lastProblem;
    private DateTime _lastProblemAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleNotifier"/> class.
    /// </summary>
    /// <param name="discord">Discord webhook sender.</param>
    /// <param name="activity">Activity log.</param>
    public SubtitleNotifier(DiscordNotifier discord, ActivityLog activity)
    {
        _discord = discord;
        _activity = activity;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Reports the subtitles downloaded in a run, if that's switched on.</summary>
    /// <param name="downloads">What was downloaded, in download order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DownloadsAsync(IReadOnlyList<DownloadedSubtitle> downloads, CancellationToken cancellationToken)
    {
        var config = Config;
        return config.NotifyDownloads && DiscordMessageBuilder.BuildDownloads(downloads, config.PublicServerUrl) is { } message
            ? SendAsync(message, $"Told Discord about {downloads.Count} downloaded {(downloads.Count == 1 ? "subtitle" : "subtitles")}", cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Reports that the daily download allowance ran out, if problems are switched on.</summary>
    /// <param name="until">When it renews (UTC).</param>
    public void LimitReached(DateTime until)
    {
        if (Config.NotifyProblems)
        {
            _ = SendAsync(DiscordMessageBuilder.BuildLimitReached(until), "Told Discord the daily download limit is used up", CancellationToken.None);
        }
    }

    /// <summary>Reports a failed login, if problems are switched on and it wasn't just reported.</summary>
    /// <param name="error">Why it failed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task LoginFailedAsync(string error, CancellationToken cancellationToken)
    {
        if (!Config.NotifyProblems || !IsNewProblem(error, DateTime.UtcNow))
        {
            return Task.CompletedTask;
        }

        return SendAsync(DiscordMessageBuilder.BuildLoginFailed(error), "Told Discord the OpenSubtitles login is failing", cancellationToken);
    }

    /// <summary>Sends a test message to the saved webhook.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task. Throws with a readable message if it couldn't send.</returns>
    public Task TestAsync(CancellationToken cancellationToken)
        => _discord.SendAsync(Config.DiscordWebhookUrl, [DiscordMessageBuilder.BuildTest()], cancellationToken);

    /// <summary>Whether a problem should be reported: it's different from the last one, or that was a while ago.</summary>
    /// <param name="problem">The problem.</param>
    /// <param name="now">Current time (UTC).</param>
    /// <returns>True to report it.</returns>
    internal bool IsNewProblem(string problem, DateTime now)
    {
        lock (_lock)
        {
            if (_lastProblem == problem && now - _lastProblemAt < RepeatProblemAfter)
            {
                return false;
            }

            _lastProblem = problem;
            _lastProblemAt = now;
            return true;
        }
    }

    private async Task SendAsync(JsonObject message, string done, CancellationToken cancellationToken)
    {
        var url = Config.DiscordWebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            await _discord.SendAsync(url, [message], cancellationToken).ConfigureAwait(false);
            _activity.Info(done);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activity.Error("Couldn't post to Discord", ex);
        }
    }
}
