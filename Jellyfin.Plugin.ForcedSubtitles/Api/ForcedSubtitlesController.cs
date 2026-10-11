using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="ForcedSubtitlesController"/> class.
    /// </summary>
    /// <param name="store">Scan state store.</param>
    /// <param name="client">OpenSubtitles client.</param>
    /// <param name="processor">Processor.</param>
    public ForcedSubtitlesController(ScanStore store, OpenSubtitlesClient client, ForcedSubtitleProcessor processor)
    {
        _store = store;
        _client = client;
        _processor = processor;
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

    /// <summary>Says where the API key and login are coming from (without revealing them).</summary>
    /// <returns>Credential sources.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetStatus()
    {
        var c = _client.Credentials;
        return new { c.ApiKeySource, c.LoginSource, c.Username };
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

    /// <summary>Starts a full check in the background.</summary>
    /// <returns>Accepted.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult Run()
    {
        _processor.RunInBackground(false);
        return Accepted();
    }

    /// <summary>Forgets scan history so every video is re-checked on the next run.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Reset()
    {
        _store.Clear();
        return NoContent();
    }
}
