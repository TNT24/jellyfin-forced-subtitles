using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
using Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;

namespace Jellyfin.Plugin.ForcedSubtitles.Scanning;

/// <summary>A chosen subtitle and the file to download.</summary>
/// <param name="Entry">The subtitle.</param>
/// <param name="File">Its file.</param>
public sealed record RankedSubtitle(SubtitleEntry Entry, SubtitleFile File);

/// <summary>
/// Picks the subtitle most likely to sync with the user's file.
/// </summary>
public static class SubtitleRanker
{
    private static readonly char[] Separators = [' ', '.', '-', '_', '[', ']', '(', ')'];

    /// <summary>Picks the best candidate, or null if none is acceptable.</summary>
    /// <param name="results">Search results.</param>
    /// <param name="language">OpenSubtitles language code that was searched.</param>
    /// <param name="forced">Whether a forced subtitle is wanted.</param>
    /// <param name="videoPath">Path of the video, for release-name matching.</param>
    /// <param name="config">Plugin settings.</param>
    /// <param name="requireHash">Only accept exact file-hash matches regardless of settings.</param>
    /// <returns>The best subtitle or null.</returns>
    public static RankedSubtitle? PickBest(IEnumerable<SubtitleEntry> results, string language, bool forced, string? videoPath, PluginConfiguration config, bool requireHash = false)
    {
        var fileTokens = Tokens(Path.GetFileNameWithoutExtension(videoPath));

        return results
            .Where(r => string.Equals(r.Attributes.Language, language, StringComparison.OrdinalIgnoreCase))
            // Strict: the forced flag must match what we asked for, so we never save a full subtitle as forced.
            .Where(r => r.Attributes.ForeignPartsOnly == forced)
            .Where(r => !config.SkipMachineTranslated || (!r.Attributes.AiTranslated && !r.Attributes.MachineTranslated))
            .Where(r => !(config.RequireHashMatch || requireHash) || r.Attributes.MovieHashMatch == true)
            // Multi-CD subtitles don't map onto a single video file.
            .Where(r => r.Attributes.Files.Count == 1)
            .Select(r => (Entry: r, Score: Score(r.Attributes, fileTokens)))
            .OrderByDescending(x => x.Score)
            .Select(x => new RankedSubtitle(x.Entry, x.Entry.Attributes.Files[0]))
            .FirstOrDefault();
    }

    private static double Score(SubtitleAttributes a, HashSet<string> fileTokens)
    {
        double score = 0;
        if (a.MovieHashMatch == true)
        {
            score += 1000;
        }

        if (a.FromTrusted == true)
        {
            score += 50;
        }

        if (!a.HearingImpaired)
        {
            score += 20;
        }

        // Overlap between release name and file name (e.g. "1080p", "BluRay", group name).
        var releaseTokens = Tokens(a.Release);
        if (releaseTokens.Count > 0 && fileTokens.Count > 0)
        {
            score += 200.0 * releaseTokens.Count(fileTokens.Contains) / releaseTokens.Count;
        }

        score += 10 * Math.Log10(1 + a.DownloadCount);
        score += a.Ratings ?? 0;
        return score;
    }

    private static HashSet<string> Tokens(string? text)
        => string.IsNullOrEmpty(text)
            ? []
            : text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).ToHashSet();
}
