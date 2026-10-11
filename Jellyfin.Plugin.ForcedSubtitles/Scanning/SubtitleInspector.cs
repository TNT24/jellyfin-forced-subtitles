using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.ForcedSubtitles.Scanning;

/// <summary>What subtitles a video already has.</summary>
/// <param name="HasForced">A forced subtitle in the target language exists (embedded or external).</param>
/// <param name="HasFull">A regular subtitle in the target language exists.</param>
/// <param name="AudioLanguage">The default audio track's language, if known.</param>
/// <param name="AudioIsTarget">The default audio is in the target language (or unknown).</param>
/// <param name="IsAnalyzed">Jellyfin has probed the file (it has at least one audio track).</param>
public sealed record SubtitleState(bool HasForced, bool HasFull, string? AudioLanguage, bool AudioIsTarget, bool IsAnalyzed);

/// <summary>
/// Inspects a video's streams and builds OpenSubtitles queries for it.
/// </summary>
public sealed class SubtitleInspector
{
    private readonly ILocalizationManager _localization;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleInspector"/> class.
    /// </summary>
    /// <param name="localization">Localization manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    public SubtitleInspector(ILocalizationManager localization, IMediaSourceManager mediaSourceManager, ILibraryManager libraryManager)
    {
        _localization = localization;
        _mediaSourceManager = mediaSourceManager;
        _libraryManager = libraryManager;
    }

    /// <summary>Works out which subtitles a video already has in <paramref name="language"/>.</summary>
    /// <param name="video">The video.</param>
    /// <param name="language">Target language (any form Jellyfin understands, e.g. "eng").</param>
    /// <returns>The current subtitle state.</returns>
    public SubtitleState Inspect(Video video, string language)
    {
        var aliases = GetAliases(language);
        var streams = _mediaSourceManager.GetMediaStreams(video.Id);

        bool hasForced = false, hasFull = false;
        foreach (var s in streams.Where(s => s.Type == MediaStreamType.Subtitle))
        {
            if (!Matches(s.Language, aliases))
            {
                continue;
            }

            if (IsForced(s))
            {
                hasForced = true;
            }
            else
            {
                hasFull = true;
            }
        }

        var audio = streams.Where(s => s.Type == MediaStreamType.Audio)
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.Index)
            .FirstOrDefault();
        var audioLang = audio?.Language;
        var audioUnknown = string.IsNullOrWhiteSpace(audioLang)
            || audioLang.Equals("und", StringComparison.OrdinalIgnoreCase)
            || audioLang.Equals("unknown", StringComparison.OrdinalIgnoreCase);

        return new SubtitleState(hasForced, hasFull, audioLang, audioUnknown || Matches(audioLang, aliases), audio is not null);
    }

    /// <summary>Builds an OpenSubtitles search for a video.</summary>
    /// <param name="video">Movie or episode.</param>
    /// <param name="language">Target language.</param>
    /// <param name="foreignParts">Forced filter.</param>
    /// <returns>The query, or null if the video type is unsupported.</returns>
    public SubtitleQuery? BuildQuery(Video video, string language, ForeignPartsFilter foreignParts)
    {
        var query = new SubtitleQuery
        {
            Language = ToOpenSubtitlesLanguage(language),
            ForeignParts = foreignParts,
            MovieHash = MovieHash.TryCompute(video.Path),
            Year = video.ProductionYear,
        };

        switch (video)
        {
            case Episode episode:
                query.IsEpisode = true;
                query.SeasonNumber = episode.ParentIndexNumber;
                query.EpisodeNumber = episode.IndexNumber;
                var series = episode.Series;
                if (series is not null)
                {
                    query.ParentImdbId = ParseImdb(series.GetProviderId(MetadataProvider.Imdb));
                    query.ParentTmdbId = ParseInt(series.GetProviderId(MetadataProvider.Tmdb));
                    query.Text = series.Name;
                }

                // Prefer series id + season/episode: episode IMDb ids from metadata are often missing or
                // actually the series id, which would return every episode of the show.
                if (query.ParentImdbId is null && query.ParentTmdbId is null)
                {
                    query.ImdbId = ParseImdb(episode.GetProviderId(MetadataProvider.Imdb));
                }

                if (query.ImdbId is null && (query.SeasonNumber is null || query.EpisodeNumber is null))
                {
                    return null;
                }

                break;
            case MediaBrowser.Controller.Entities.Movies.Movie movie:
                query.ImdbId = ParseImdb(movie.GetProviderId(MetadataProvider.Imdb));
                query.TmdbId = ParseInt(movie.GetProviderId(MetadataProvider.Tmdb));
                query.Text = movie.Name;
                break;
            default:
                return null;
        }

        return query;
    }

    /// <summary>Finds the library video at a path.</summary>
    /// <param name="path">Media path.</param>
    /// <returns>The video, or null.</returns>
    public Video? FindVideo(string? path)
        => string.IsNullOrEmpty(path) ? null : _libraryManager.FindByPath(path, false) as Video;

    /// <summary>Converts a Jellyfin language to an OpenSubtitles language code.</summary>
    /// <param name="language">Language in any form.</param>
    /// <returns>OpenSubtitles code such as "en" or "pt-br".</returns>
    public string ToOpenSubtitlesLanguage(string language)
    {
        var l = language.Trim().ToLowerInvariant();
        switch (l)
        {
            case "pob" or "pt-br" or "ptbr":
                return "pt-br";
            case "por" or "pt":
                return "pt-pt";
            case "zho" or "chi" or "zh" or "zh-cn":
                return "zh-cn";
            case "zh-tw":
                return "zh-tw";
        }

        var info = _localization.FindLanguageInfo(l);
        return string.IsNullOrEmpty(info?.TwoLetterISOLanguageName) ? l : info.TwoLetterISOLanguageName.ToLowerInvariant();
    }

    /// <summary>Converts a language to Jellyfin's three-letter form.</summary>
    /// <param name="language">Language in any form.</param>
    /// <returns>ISO 639-2 code.</returns>
    public string ToThreeLetter(string language)
    {
        var l = language.Trim().ToLowerInvariant();
        if (l is "pt-br")
        {
            return "pob";
        }

        var info = _localization.FindLanguageInfo(l.Split('-')[0]);
        return string.IsNullOrEmpty(info?.ThreeLetterISOLanguageName) ? l : info.ThreeLetterISOLanguageName;
    }

    private static bool IsForced(MediaStream s)
    {
        if (s.IsForced)
        {
            return true;
        }

        // Many releases don't set the forced flag but name the track or file accordingly.
        static bool Hints(string? text) => !string.IsNullOrEmpty(text)
            && (text.Contains("forced", StringComparison.OrdinalIgnoreCase)
                || text.Contains("foreign", StringComparison.OrdinalIgnoreCase));

        return Hints(s.Title)
            || (s.IsExternal && Hints(Path.GetFileName(s.Path)));
    }

    private static bool Matches(string? language, HashSet<string> aliases)
        => !string.IsNullOrWhiteSpace(language) && aliases.Contains(language.Trim());

    private static int? ParseImdb(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return ParseInt(id.Trim().TrimStart('t', 'T'));
    }

    private static int? ParseInt(string? s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    private HashSet<string> GetAliases(string language)
    {
        var names = new List<string?> { language };
        var info = _localization.FindLanguageInfo(language);
        if (info is not null)
        {
            names.AddRange([info.TwoLetterISOLanguageName, info.ThreeLetterISOLanguageName, info.DisplayName, info.Name]);
            names.AddRange(info.ThreeLetterISOLanguageNames ?? []);
        }

        return names.Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
