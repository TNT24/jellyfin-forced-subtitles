using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;
using Jellyfin.Plugin.ForcedSubtitles.Scanning;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ForcedSubtitles.Providers;

/// <summary>
/// Subtitle provider for Jellyfin's "Search subtitles" dialog. Results are tagged Forced
/// when they cover foreign parts only, and forced downloads are saved as *.forced.srt.
/// </summary>
public class OpenSubtitlesForcedProvider : ISubtitleProvider
{
    private readonly OpenSubtitlesClient _client;
    private readonly SubtitleInspector _inspector;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSubtitlesForcedProvider"/> class.
    /// </summary>
    /// <param name="client">OpenSubtitles client.</param>
    /// <param name="inspector">Subtitle inspector.</param>
    public OpenSubtitlesForcedProvider(OpenSubtitlesClient client, SubtitleInspector inspector)
    {
        _client = client;
        _inspector = inspector;
    }

    /// <inheritdoc />
    public string Name => "OpenSubtitles (Forced aware)";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        if (!_client.IsConfigured)
        {
            return [];
        }

        var video = _inspector.FindVideo(request.MediaPath);
        if (video is null)
        {
            return [];
        }

        // Jellyfin's automatic downloader takes the top result and doesn't understand forced,
        // so only offer forced subtitles when a person is choosing manually.
        var filter = request.IsAutomated ? ForeignPartsFilter.Exclude : ForeignPartsFilter.Include;
        var query = _inspector.BuildQuery(video, string.IsNullOrEmpty(request.Language) ? "eng" : request.Language, filter);
        if (query is null)
        {
            return [];
        }

        var results = await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        return results
            .Where(r => r.Attributes.Files.Count == 1)
            .Where(r => !request.IsPerfectMatch || r.Attributes.MovieHashMatch == true)
            .OrderByDescending(r => r.Attributes.MovieHashMatch == true)
            .ThenByDescending(r => r.Attributes.DownloadCount)
            .Select(r =>
            {
                var a = r.Attributes;
                var three = _inspector.ToThreeLetter(a.Language ?? query.Language);
                return new RemoteSubtitleInfo
                {
                    Id = string.Join('_', a.Files[0].FileId.ToString(CultureInfo.InvariantCulture), a.ForeignPartsOnly ? "1" : "0", a.HearingImpaired ? "1" : "0", three),
                    ProviderName = Name,
                    Name = (a.ForeignPartsOnly ? "[FORCED] " : string.Empty) + (a.Release ?? a.Files[0].FileName),
                    ThreeLetterISOLanguageName = three,
                    Format = "srt",
                    Author = a.Uploader?.Name,
                    Comment = a.Comments,
                    DateCreated = a.UploadDate,
                    CommunityRating = a.Ratings,
                    FrameRate = a.Fps,
                    DownloadCount = a.DownloadCount,
                    IsHashMatch = a.MovieHashMatch,
                    AiTranslated = a.AiTranslated,
                    MachineTranslated = a.MachineTranslated,
                    Forced = a.ForeignPartsOnly,
                    HearingImpaired = a.HearingImpaired
                };
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        // id = fileId_forced_hearingImpaired_language
        var parts = id.Split('_');
        if (parts.Length != 4 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileId))
        {
            throw new ArgumentException("Invalid subtitle id: " + id, nameof(id));
        }

        var (content, _) = await _client.DownloadAsync(fileId, cancellationToken).ConfigureAwait(false);
        return new SubtitleResponse
        {
            Format = "srt",
            IsForced = parts[1] == "1",
            IsHearingImpaired = parts[2] == "1",
            Language = parts[3],
            Stream = content
        };
    }
}
