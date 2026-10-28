using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ForcedSubtitles.Notifications;
using Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;
using Jellyfin.Plugin.ForcedSubtitles.Scanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ForcedSubtitles.Api;

/// <summary>
/// Endpoints used by the plugin's settings page.
/// </summary>
[ApiController]
[Route("ForcedSubtitles")]
[Authorize(Policy = "RequiresElevation")]
public class ForcedSubtitlesController : ControllerBase
{
    private readonly ScanStore _store;
    private readonly OpenSubtitlesClient _client;
    private readonly ForcedSubtitleProcessor _processor;
    private readonly ActivityLog _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForcedSubtitlesController"/> class.
    /// </summary>
    /// <param name="store">Scan state store.</param>
    /// <param name="client">OpenSubtitles client.</param>
    /// <param name="processor">Processor.</param>
    /// <param name="activity">Activity log.</param>
    public ForcedSubtitlesController(ScanStore store, OpenSubtitlesClient client, ForcedSubtitleProcessor processor, ActivityLog activity)
    {
        _store = store;
        _client = client;
        _processor = processor;
        _activity = activity;
    }

    /// <summary>Gets counts per status and the most recent non-trivial results.</summary>
    /// <returns>The report.</returns>
    [HttpGet("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetReport()
    {
        var all = _store.Snapshot();
        return new
        {
            Counts = all.GroupBy(r => r.Value.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            Recent = all
                .Where(r => r.Value.Status != ScanStatus.AlreadyHas)
                .OrderByDescending(r => r.Value.LastChecked)
                .Take(200)
                .Select(r => new
                {
                    ItemId = r.Key,
                    r.Value.Name,
                    Status = r.Value.Status.ToString(),
                    r.Value.Detail,
                    r.Value.LastChecked
                })
        };
    }

    /// <summary>
    /// Says where the API key and login are coming from (without revealing them), whether a check
    /// is running, and when downloads resume if the daily allowance is used up.
    /// </summary>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var c = _client.Credentials;
        return new { c.ApiKeySource, c.LoginSource, c.Username, _processor.IsRunning, _client.DownloadsBlockedUntil };
    }

    /// <summary>Gets the plugin's activity log, newest first.</summary>
    /// <returns>Log entries.</returns>
    [HttpGet("Log")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetLog()
    {
        return _activity.Recent()
            .Select(e => new { e.Time, Level = e.Level.ToString(), e.Message })
            .ToList();
    }

    /// <summary>Clears the activity log.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Log/Clear")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearLog()
    {
        _activity.Clear();
        return NoContent();
    }

    /// <summary>Logs in to OpenSubtitles and reports the daily download allowance.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the test.</returns>
    [HttpPost("Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Test(CancellationToken cancellationToken)
    {
        try
        {
            var info = await _client.GetUserInfoAsync(cancellationToken).ConfigureAwait(false);
            return new { Ok = true, Message = $"Connected as {_client.Credentials.Username} ({info.Level}). {info.RemainingDownloads} of {info.AllowedDownloads} downloads left today." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { Ok = false, ex.Message };
        }
    }

    /// <summary>Sends a test message to the Discord webhook in the saved settings.</summary>
    /// <param name="notifier">Discord notifications.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the test.</returns>
    [HttpPost("TestDiscord")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TestDiscord([FromServices] SubtitleNotifier notifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.DiscordWebhookUrl))
        {
            return new { Ok = false, Message = "Enter a Discord webhook URL first." };
        }

        try
        {
            await notifier.TestAsync(cancellationToken).ConfigureAwait(false);
            return new { Ok = true, Message = "Sent. Check your Discord channel." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { Ok = false, ex.Message };
        }
    }

    /// <summary>Starts a full check in the background, unless there is a reason it can't do anything.</summary>
    /// <returns>Whether it started, and a message for the user.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Run()
    {
        if (!_client.IsConfigured)
        {
            return new { Started = false, Message = "No OpenSubtitles API key. Enter one under Advanced, then save." };
        }

        if (_processor.IsRunning)
        {
            return new { Started = false, Message = "A check is already running. Press Refresh to see its progress." };
        }

        if (_client.DownloadsBlockedUntil is DateTime until)
        {
            return new
            {
                Started = false,
                Message = $"The daily download limit is used up until {ForcedSubtitleProcessor.FormatUtc(until)}. The next run after that carries on.",
            };
        }

        _processor.RunInBackground(false);
        return new { Started = true, Message = "Check started in the background. Press Refresh in a minute to see results." };
    }

    /// <summary>Forgets scan history so every video is re-checked on the next run.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Reset()
    {
        _store.Clear();
        _activity.Info("History cleared; every video will be checked again on the next run");
        return NoContent();
    }
}
