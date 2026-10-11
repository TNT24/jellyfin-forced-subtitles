namespace Jellyfin.Plugin.ForcedSubtitles.Configuration;

/// <summary>Where downloaded subtitles are written.</summary>
public enum SubtitleLocation
{
    /// <summary>Jellyfin's internal metadata folder; media folders are left untouched.</summary>
    MetadataFolder,

    /// <summary>Next to the video file.</summary>
    MediaFolder,

    /// <summary>Follow the library's "Save subtitles into media folders" setting.</summary>
    LibrarySetting
}
