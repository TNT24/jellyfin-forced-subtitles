using System.IO;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
using Jellyfin.Plugin.ForcedSubtitles.Notifications;
using Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;
using Jellyfin.Plugin.ForcedSubtitles.Scanning;
using Xunit;

namespace Jellyfin.Plugin.ForcedSubtitles.Tests;

public class QueryStringTests
{
    [Fact]
    public void Movie_ParamsAreSortedAndLowercase()
    {
        var qs = OpenSubtitlesClient.BuildQueryString(new SubtitleQuery
        {
            Language = "EN",
            ImdbId = 113277,
            TmdbId = 949,
            Text = "Heat",
            MovieHash = "ABCDEF0123456789",
            ForeignParts = ForeignPartsFilter.Only
        });

        Assert.Equal("foreign_parts_only=only&imdb_id=113277&languages=en&moviehash=abcdef0123456789&type=movie", qs);
    }

    [Fact]
    public void Movie_WithoutIds_FallsBackToTitleAndYear()
    {
        var qs = OpenSubtitlesClient.BuildQueryString(new SubtitleQuery { Text = "Some Film", Year = 2001 });
        Assert.Equal("foreign_parts_only=include&languages=en&query=some%20film&type=movie&year=2001", qs);
    }

    [Fact]
    public void Episode_UsesParentIdWithSeasonAndEpisode()
    {
        var qs = OpenSubtitlesClient.BuildQueryString(new SubtitleQuery
        {
            IsEpisode = true,
            ParentImdbId = 903747,
            SeasonNumber = 2,
            EpisodeNumber = 5,
            ForeignParts = ForeignPartsFilter.Exclude
        });

        Assert.Equal("episode_number=5&foreign_parts_only=exclude&languages=en&parent_imdb_id=903747&season_number=2&type=episode", qs);
    }

    [Fact]
    public void StrongId_OnlyWhenAnIdIsPresent()
    {
        Assert.False(new SubtitleQuery { Text = "x" }.HasStrongId);
        Assert.True(new SubtitleQuery { ParentTmdbId = 1 }.HasStrongId);
    }
}

public class RankerTests
{
    private static SubtitleEntry Sub(int fileId, bool forced, string release = "x", bool hash = false, int downloads = 0, string lang = "en", bool machine = false, int files = 1)
    {
        var a = new SubtitleAttributes
        {
            Language = lang,
            ForeignPartsOnly = forced,
            Release = release,
            MovieHashMatch = hash,
            DownloadCount = downloads,
            MachineTranslated = machine
        };
        for (int i = 0; i < files; i++)
        {
            a.Files.Add(new SubtitleFile { FileId = fileId + i });
        }

        return new SubtitleEntry { Id = fileId.ToString(System.Globalization.CultureInfo.InvariantCulture), Attributes = a };
    }

    private static readonly PluginConfiguration Config = new();

    [Fact]
    public void NeverPicksFullSubtitleWhenForcedWanted()
    {
        var best = SubtitleRanker.PickBest([Sub(1, forced: false, downloads: 99999)], "en", forced: true, "/m/Heat.mkv", Config);
        Assert.Null(best);
    }

    [Fact]
    public void PrefersHashMatch()
    {
        var best = SubtitleRanker.PickBest(
            [Sub(1, true, downloads: 50000), Sub(2, true, hash: true)],
            "en", true, "/m/Heat.mkv", Config);
        Assert.Equal(2, best!.File.FileId);
    }

    [Fact]
    public void PrefersReleaseNameMatchingFile()
    {
        var best = SubtitleRanker.PickBest(
            [Sub(1, true, "Heat.1995.720p.WEB-DL", downloads: 3000), Sub(2, true, "Heat.1995.1080p.BluRay.x264-GROUP", downloads: 100)],
            "en", true, "/m/Heat.1995.1080p.BluRay.x264-GROUP.mkv", Config);
        Assert.Equal(2, best!.File.FileId);
    }

    [Fact]
    public void FiltersLanguageMachineTranslationAndMultiCd()
    {
        var best = SubtitleRanker.PickBest(
            [Sub(1, true, lang: "fr"), Sub(2, true, machine: true), Sub(3, true, files: 2)],
            "en", true, "/m/a.mkv", Config);
        Assert.Null(best);
    }

    [Fact]
    public void RequireHash_RejectsNonHashResults()
    {
        var best = SubtitleRanker.PickBest([Sub(1, true, downloads: 1000)], "en", true, "/m/a.mkv", Config, requireHash: true);
        Assert.Null(best);
    }
}

public class MovieHashTests
{
    [Fact]
    public void SmallOrMissingFile_ReturnsNull()
    {
        Assert.Null(MovieHash.TryCompute("/does/not/exist.mkv"));
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, new byte[1000]);
        Assert.Null(MovieHash.TryCompute(tmp));
        File.Delete(tmp);
    }

    [Fact]
    public void ZeroFile_HashIsFileSize()
    {
        // All-zero content contributes nothing, so the hash is just the length.
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, new byte[200_000]);
        Assert.Equal(200_000L.ToString("x16"), MovieHash.TryCompute(tmp));
        File.Delete(tmp);
    }

    [Fact]
    public void SumsFirstAndLastChunksLittleEndian()
    {
        var data = new byte[200_000];
        data[0] = 1;                    // +1 in first chunk
        data[200_000 - 8] = 2;          // +2 in last chunk (8-byte aligned from end)
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, data);
        Assert.Equal((200_000L + 3).ToString("x16"), MovieHash.TryCompute(tmp));
        File.Delete(tmp);
    }
}

public class RetryDelayTests
{
    private static readonly System.DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, System.TimeSpan.Zero);

    [Fact]
    public void NoHeader_UsesShortBackoff()
    {
        Assert.Equal(System.TimeSpan.FromSeconds(1), OpenSubtitlesClient.GetRetryDelay(null, 0, Now));
        Assert.Equal(System.TimeSpan.FromSeconds(3), OpenSubtitlesClient.GetRetryDelay(null, 2, Now));
    }

    [Fact]
    public void DeltaHeader_IsHonoured()
    {
        var header = new System.Net.Http.Headers.RetryConditionHeaderValue(System.TimeSpan.FromSeconds(7));
        Assert.Equal(System.TimeSpan.FromSeconds(7), OpenSubtitlesClient.GetRetryDelay(header, 0, Now));
    }

    [Fact]
    public void DateHeader_IsHonoured()
    {
        var header = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddSeconds(5));
        Assert.Equal(System.TimeSpan.FromSeconds(5), OpenSubtitlesClient.GetRetryDelay(header, 0, Now));
    }

    [Fact]
    public void LongWait_IsCapped()
    {
        var header = new System.Net.Http.Headers.RetryConditionHeaderValue(System.TimeSpan.FromHours(1));
        Assert.Equal(System.TimeSpan.FromSeconds(30), OpenSubtitlesClient.GetRetryDelay(header, 0, Now));
    }

    [Fact]
    public void PastDate_FallsBackToBackoff()
    {
        var header = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddSeconds(-5));
        Assert.Equal(System.TimeSpan.FromSeconds(2), OpenSubtitlesClient.GetRetryDelay(header, 1, Now));
    }
}

public class SearchAgainTests
{
    private static readonly System.DateTime Now = new(2026, 10, 11, 12, 0, 0, System.DateTimeKind.Utc);
    private static readonly PluginConfiguration Config = new() { RecheckAfterDays = 14 };

    private static bool Search(ScanStatus? status, double hoursAgo, string? detail = null, string? savedPath = null, bool fileExists = true)
    {
        var record = status is null ? null : new ScanRecord
        {
            Status = status.Value,
            LastChecked = Now.AddHours(-hoursAgo),
            Detail = detail,
            SavedPath = savedPath
        };
        return ForcedSubtitleProcessor.ShouldSearch(record, Config, Now, _ => fileExists);
    }

    [Fact]
    public void NeverChecked_Searches() => Assert.True(Search(null, 0));

    [Fact]
    public void HadSubtitleBefore_Searches() => Assert.True(Search(ScanStatus.AlreadyHas, 1));

    [Fact]
    public void Downloaded_FileStillThere_Waits() => Assert.False(Search(ScanStatus.Downloaded, 1, savedPath: "a.srt"));

    [Fact]
    public void Downloaded_FileDeleted_Searches() => Assert.True(Search(ScanStatus.Downloaded, 1, savedPath: "a.srt", fileExists: false));

    [Fact]
    public void Downloaded_OldRecordWithoutPath_Waits() => Assert.False(Search(ScanStatus.Downloaded, 1000));

    [Fact]
    public void NotFound_WaitsRecheckDays()
    {
        Assert.False(Search(ScanStatus.NotFound, 13 * 24));
        Assert.True(Search(ScanStatus.NotFound, 14 * 24));
    }

    [Fact]
    public void Error_WaitsOneDay()
    {
        Assert.False(Search(ScanStatus.Error, 23, "Access denied"));
        Assert.True(Search(ScanStatus.Error, 24, "Access denied"));
    }

    [Fact]
    public void QuotaError_RetriesNextRun() => Assert.True(Search(ScanStatus.Error, 1, "Quota reached, will retry next run"));
}

public class QuotaResetTests
{
    private static readonly System.DateTime Now = new(2026, 10, 11, 12, 0, 0, System.DateTimeKind.Utc);

    [Fact]
    public void UtcResetTime_IsUsed()
    {
        var reset = new System.DateTime(2026, 10, 11, 18, 30, 0, System.DateTimeKind.Utc);
        Assert.Equal(reset, OpenSubtitlesClient.GetDownloadsBlockedUntil(reset, Now));
    }

    [Fact]
    public void ResetTimeWithoutZone_IsTreatedAsUtc()
    {
        var reset = new System.DateTime(2026, 10, 11, 18, 30, 0, System.DateTimeKind.Unspecified);
        var until = OpenSubtitlesClient.GetDownloadsBlockedUntil(reset, Now);
        Assert.Equal(System.DateTimeKind.Utc, until.Kind);
        Assert.Equal(18, until.Hour);
    }

    [Fact]
    public void MissingOrPastResetTime_WaitsAnHour()
    {
        Assert.Equal(Now.AddHours(1), OpenSubtitlesClient.GetDownloadsBlockedUntil(null, Now));
        Assert.Equal(Now.AddHours(1), OpenSubtitlesClient.GetDownloadsBlockedUntil(Now.AddMinutes(-5), Now));
    }
}

public class DownloadLimitPersistenceTests
{
    private static readonly System.DateTime Now = new(2026, 10, 11, 12, 0, 0, System.DateTimeKind.Utc);

    [Fact]
    public void SavedTime_RoundTripsAsUtc()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "download-limit.txt");
        try
        {
            var until = new System.DateTime(2026, 10, 11, 18, 30, 0, System.DateTimeKind.Utc);
            OpenSubtitlesClient.WriteBlockedUntil(path, until);
            var read = OpenSubtitlesClient.ReadBlockedUntil(path);
            Assert.Equal(until, read);
            Assert.Equal(System.DateTimeKind.Utc, read!.Value.Kind);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void MissingOrGarbledFile_ReadsAsNotBlocked()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Assert.Null(OpenSubtitlesClient.ReadBlockedUntil(path));
        try
        {
            File.WriteAllText(path, "not a date");
            Assert.Null(OpenSubtitlesClient.ReadBlockedUntil(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResumeWait_IsFiveMinutesAfterReset()
    {
        Assert.Equal(System.TimeSpan.FromMinutes(65), DownloadLimitMonitor.GetResumeWait(Now.AddHours(1), Now));
    }

    [Fact]
    public void ResumeWait_IsZeroOnceRenewed_AndCappedWhenFarOff()
    {
        Assert.Equal(System.TimeSpan.Zero, DownloadLimitMonitor.GetResumeWait(Now.AddHours(-1), Now));
        Assert.Equal(System.TimeSpan.FromDays(2), DownloadLimitMonitor.GetResumeWait(Now.AddDays(30), Now));
    }
}

public class DiscordMessageTests
{
    private static DownloadedSubtitle Download(string name, bool forced = true, int? remaining = null)
        => new(Guid.NewGuid(), name, forced, "eng", false, remaining);

    [Fact]
    public void NothingDownloaded_NoMessage()
    {
        Assert.Null(DiscordMessageBuilder.BuildDownloads([], string.Empty));
    }

    [Fact]
    public void Downloads_CountsEachKind_AndShowsTheLastRemaining()
    {
        var msg = DiscordMessageBuilder.BuildDownloads([Download("A", remaining: 9), Download("B", remaining: 8), Download("C", forced: false)], string.Empty)!;
        Assert.Equal("💬 **Downloaded 2 forced subtitles and 1 full subtitle**", msg["content"]!.GetValue<string>());
        Assert.Equal("8 downloads left today", msg["embeds"]![0]!["footer"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Downloads_LinkToTheVideo_WhenServerAddressIsSet()
    {
        var d = Download("Heat (1995)");
        var description = DiscordMessageBuilder.BuildDownloads([d], "https://jf.example.com/")!["embeds"]![0]!["description"]!.GetValue<string>();
        Assert.Contains($"(https://jf.example.com/web/#/details?id={d.ItemId:N})", description);
    }

    [Fact]
    public void LongRuns_AreCutShort()
    {
        var downloads = Enumerable.Range(0, 40).Select(i => Download("Film " + i)).ToList();
        var description = DiscordMessageBuilder.BuildDownloads(downloads, string.Empty)!["embeds"]![0]!["description"]!.GetValue<string>();
        Assert.EndsWith("…and 15 more", description);
    }

    [Fact]
    public void NeverPingsAnyone()
    {
        var msg = DiscordMessageBuilder.BuildDownloads([Download("@everyone")], string.Empty)!;
        Assert.Empty(msg["allowed_mentions"]!["parse"]!.AsArray());
    }

    [Fact]
    public void Timestamp_UsesDiscordFormat()
    {
        Assert.Equal("<t:0:R> (<t:0:f>)", DiscordMessageBuilder.Timestamp(DateTime.UnixEpoch));
    }

    [Fact]
    public void SameProblem_IsReportedOncePerDay()
    {
        var notifier = new SubtitleNotifier(null!, null!);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(notifier.IsNewProblem("bad password", now));
        Assert.False(notifier.IsNewProblem("bad password", now.AddHours(5)));
        Assert.True(notifier.IsNewProblem("other", now.AddHours(6)));
        Assert.True(notifier.IsNewProblem("bad password", now.AddHours(7)));
        Assert.True(notifier.IsNewProblem("bad password", now.AddHours(32)));
    }
}
