using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.ForcedSubtitles.Notifications;

/// <summary>One subtitle downloaded during a run.</summary>
/// <param name="ItemId">The video.</param>
/// <param name="Name">Display name, e.g. "Heat (1995)" or "Show S01E02 - Title".</param>
/// <param name="Forced">Whether it's a forced (foreign dialogue only) subtitle.</param>
/// <param name="Language">The subtitle language, e.g. "eng".</param>
/// <param name="HashMatch">Whether it matched the file hash.</param>
/// <param name="Remaining">Downloads left today after this one, if OpenSubtitles said.</param>
public sealed record DownloadedSubtitle(Guid ItemId, string Name, bool Forced, string Language, bool HashMatch, int? Remaining);

/// <summary>
/// Turns run results into Discord webhook messages. No network access, so it can be unit tested.
/// </summary>
public static class DiscordMessageBuilder
{
    // Discord allows 4096 characters per embed description; leave room for the "…and N more" line.
    private const int MaxDescription = 3800;
    private const int ListUpTo = 25;

    private const int ColorDownloaded = 0x43B581;
    private const int ColorWarning = 0xF5A623;

    /// <summary>Builds the message for subtitles downloaded in one run.</summary>
    /// <param name="downloads">What was downloaded, in download order.</param>
    /// <param name="publicServerUrl">Server address for links, or empty.</param>
    /// <returns>Webhook payload, or null when nothing was downloaded.</returns>
    public static JsonObject? BuildDownloads(IReadOnlyList<DownloadedSubtitle> downloads, string publicServerUrl)
    {
        if (downloads.Count == 0)
        {
            return null;
        }

        var baseUrl = publicServerUrl.Trim().TrimEnd('/');
        var lines = new List<string>();
        var length = 0;
        foreach (var d in downloads.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var line = new StringBuilder(Link(d.Name, baseUrl, d.ItemId))
                .Append(" · ")
                .Append(d.Forced ? "forced" : "full")
                .Append(' ')
                .Append(d.Language);
            if (d.HashMatch)
            {
                line.Append(" · exact match");
            }

            if (lines.Count == ListUpTo || length + line.Length > MaxDescription)
            {
                break;
            }

            lines.Add(line.ToString());
            length += line.Length + 1;
        }

        if (downloads.Count > lines.Count)
        {
            lines.Add($"…and {(downloads.Count - lines.Count).ToString(CultureInfo.InvariantCulture)} more");
        }

        var forced = downloads.Count(d => d.Forced);
        var full = downloads.Count - forced;
        var kinds = new List<string>();
        AddCount(kinds, forced, "forced subtitle", "forced subtitles");
        AddCount(kinds, full, "full subtitle", "full subtitles");

        var embed = new JsonObject
        {
            ["description"] = string.Join('\n', lines),
            ["color"] = ColorDownloaded,
        };
        if (downloads.LastOrDefault(d => d.Remaining is not null)?.Remaining is int left)
        {
            embed["footer"] = new JsonObject { ["text"] = $"{Plural(left, "download", "downloads")} left today" };
        }

        return Message("💬 **Downloaded " + string.Join(" and ", kinds) + "**", embed);
    }

    /// <summary>Builds the message for the daily download allowance running out.</summary>
    /// <param name="until">When it renews (UTC).</param>
    /// <returns>Webhook payload.</returns>
    public static JsonObject BuildLimitReached(DateTime until)
        => Message(
            $"⏳ **OpenSubtitles daily download limit reached.** Downloads resume automatically {Timestamp(until)}.",
            null);

    /// <summary>Builds the message for a run that couldn't log in to OpenSubtitles.</summary>
    /// <param name="error">Why the login failed.</param>
    /// <returns>Webhook payload.</returns>
    public static JsonObject BuildLoginFailed(string error)
        => Message(
            "⚠️ **Forced Subtitles can't log in to OpenSubtitles**, so no subtitles are being downloaded.",
            new JsonObject
            {
                ["description"] = Escape(Truncate(error, 1000)) + "\n\nCheck the username and password in Dashboard → Plugins → Forced Subtitles.",
                ["color"] = ColorWarning,
            });

    /// <summary>Builds a test message.</summary>
    /// <returns>Webhook payload.</returns>
    public static JsonObject BuildTest()
        => Message("✅ **Forced Subtitles** can post to this channel. You'll get a message here when subtitles are downloaded.", null);

    /// <summary>Formats a time as a Discord timestamp, which every reader sees in their own time zone.</summary>
    /// <param name="utc">The time (UTC).</param>
    /// <returns>For example "&lt;t:1760000000:R&gt; (&lt;t:1760000000:f&gt;)".</returns>
    internal static string Timestamp(DateTime utc)
    {
        var seconds = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"<t:{seconds}:R> (<t:{seconds}:f>)";
    }

    private static string Link(string name, string baseUrl, Guid itemId)
    {
        var text = "**" + Escape(name) + "**";
        return baseUrl.Length > 0 && itemId != Guid.Empty
            ? $"[{text}]({baseUrl}/web/#/details?id={itemId.ToString("N", CultureInfo.InvariantCulture)})"
            : text;
    }

    private static JsonObject Message(string content, JsonObject? embed)
    {
        var message = new JsonObject
        {
            ["username"] = "Jellyfin Forced Subtitles",
            ["content"] = content,

            // Titles could contain "@everyone"; never let a notification ping anyone.
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
        if (embed is not null)
        {
            message["embeds"] = new JsonArray(embed);
        }

        return message;
    }

    private static void AddCount(List<string> parts, int count, string one, string many)
    {
        if (count > 0)
        {
            parts.Add(Plural(count, one, many));
        }
    }

    private static string Plural(int count, string one, string many)
        => count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '_' or '`' or '~' or '|' or '\\' or '>' or '[' or ']')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
