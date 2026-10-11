using System.IO;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
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
