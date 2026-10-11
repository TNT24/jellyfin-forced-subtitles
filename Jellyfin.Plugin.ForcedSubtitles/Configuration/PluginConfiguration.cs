using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ForcedSubtitles.Configuration;

/// <summary>
/// Plugin settings, edited from Dashboard → Plugins → Forced Subtitles.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the OpenSubtitles.com API key (Profile → API consumers).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the OpenSubtitles.com username. Required for downloads.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the OpenSubtitles.com password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the language forced subtitles are wanted in (ISO 639-2, e.g. "eng").</summary>
    public string ForcedLanguage { get; set; } = "eng";

    /// <summary>Gets or sets the library ids to scan. Empty means all movie and TV libraries.</summary>
    public string[] LibraryIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether newly added videos are checked after each library scan.</summary>
    public bool CheckOnLibraryScan { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether movies are scanned.</summary>
    public bool ScanMovies { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether TV episodes are scanned.</summary>
    public bool ScanEpisodes { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether full (non-forced) subtitles in <see cref="ForcedLanguage"/>
    /// are also downloaded when the video's default audio track is in another language.
    /// </summary>
    public bool DownloadFullWhenAudioForeign { get; set; } = true;

    /// <summary>Gets or sets where downloaded subtitles are saved.</summary>
    public SubtitleLocation SaveLocation { get; set; } = SubtitleLocation.MetadataFolder;

    /// <summary>Gets or sets a value indicating whether only subtitles matching the file hash are accepted.</summary>
    public bool RequireHashMatch { get; set; }

    /// <summary>Gets or sets a value indicating whether AI or machine translated subtitles are skipped.</summary>
    public bool SkipMachineTranslated { get; set; } = true;

    /// <summary>Gets or sets how many days to wait before re-searching an item where nothing was found.</summary>
    public int RecheckAfterDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets the maximum downloads per task run. Free OpenSubtitles accounts get roughly 20 per day.
    /// </summary>
    public int MaxDownloadsPerRun { get; set; } = 20;
}
