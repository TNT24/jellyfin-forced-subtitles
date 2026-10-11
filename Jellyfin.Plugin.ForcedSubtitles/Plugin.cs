using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ForcedSubtitles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ForcedSubtitles;

/// <summary>
/// Finds and downloads subtitles from OpenSubtitles.com, with special handling for forced subtitles.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Forced Subtitles";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5c6b4d1e-9f3a-4b8e-a2d7-0f1e6c3b9a47");

    /// <inheritdoc />
    public override string Description => "Identifies videos missing forced subtitles and downloads them from OpenSubtitles.com.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Forced Subtitles",
                EnableInMainMenu = true,
                MenuIcon = "subtitles",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
