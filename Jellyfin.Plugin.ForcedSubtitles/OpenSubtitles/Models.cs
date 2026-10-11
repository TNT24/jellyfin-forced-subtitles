using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;

#pragma warning disable SA1402, CS1591 // Plain DTOs for the OpenSubtitles REST API.

public enum ForeignPartsFilter
{
    /// <summary>Return both full and forced subtitles.</summary>
    Include,

    /// <summary>Return full subtitles only.</summary>
    Exclude,

    /// <summary>Return forced (foreign-parts-only) subtitles only.</summary>
    Only
}

/// <summary>What to search for. Set the most specific identifiers available.</summary>
public sealed class SubtitleQuery
{
    public string Language { get; set; } = "en";

    public int? ImdbId { get; set; }

    public int? TmdbId { get; set; }

    public int? ParentImdbId { get; set; }

    public int? ParentTmdbId { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string? Text { get; set; }

    public int? Year { get; set; }

    public string? MovieHash { get; set; }

    public ForeignPartsFilter ForeignParts { get; set; } = ForeignPartsFilter.Include;

    public bool IsEpisode { get; set; }

    /// <summary>Gets a value indicating whether the search uses an IMDb/TMDb id rather than just a title.</summary>
    public bool HasStrongId => ImdbId is not null || TmdbId is not null || ParentImdbId is not null || ParentTmdbId is not null;
}

public sealed class LoginResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }
}

public sealed class SearchResponse
{
    [JsonPropertyName("data")]
    public List<SubtitleEntry> Data { get; set; } = [];
}

public sealed class SubtitleEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("attributes")]
    public SubtitleAttributes Attributes { get; set; } = new();
}

public sealed class SubtitleAttributes
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("download_count")]
    public int DownloadCount { get; set; }

    [JsonPropertyName("hearing_impaired")]
    public bool HearingImpaired { get; set; }

    [JsonPropertyName("foreign_parts_only")]
    public bool ForeignPartsOnly { get; set; }

    [JsonPropertyName("ai_translated")]
    public bool AiTranslated { get; set; }

    [JsonPropertyName("machine_translated")]
    public bool MachineTranslated { get; set; }

    [JsonPropertyName("from_trusted")]
    public bool? FromTrusted { get; set; }

    [JsonPropertyName("moviehash_match")]
    public bool? MovieHashMatch { get; set; }

    [JsonPropertyName("fps")]
    public float? Fps { get; set; }

    [JsonPropertyName("ratings")]
    public float? Ratings { get; set; }

    [JsonPropertyName("upload_date")]
    public DateTime? UploadDate { get; set; }

    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("uploader")]
    public Uploader? Uploader { get; set; }

    [JsonPropertyName("files")]
    public List<SubtitleFile> Files { get; set; } = [];
}

public sealed class Uploader
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class SubtitleFile
{
    [JsonPropertyName("file_id")]
    public int FileId { get; set; }

    [JsonPropertyName("cd_number")]
    public int? CdNumber { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }
}

public sealed class DownloadResponse
{
    [JsonPropertyName("link")]
    public string? Link { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("remaining")]
    public int? Remaining { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("reset_time_utc")]
    public DateTime? ResetTimeUtc { get; set; }
}

public sealed class UserInfoResponse
{
    [JsonPropertyName("data")]
    public UserInfo? Data { get; set; }
}

public sealed class UserInfo
{
    [JsonPropertyName("allowed_downloads")]
    public int AllowedDownloads { get; set; }

    [JsonPropertyName("remaining_downloads")]
    public int RemainingDownloads { get; set; }

    [JsonPropertyName("level")]
    public string? Level { get; set; }

    [JsonPropertyName("vip")]
    public bool Vip { get; set; }
}

/// <summary>Thrown when the daily OpenSubtitles download allowance is used up.</summary>
public sealed class DownloadQuotaExceededException : Exception
{
    public DownloadQuotaExceededException(string message)
        : base(message)
    {
    }
}
