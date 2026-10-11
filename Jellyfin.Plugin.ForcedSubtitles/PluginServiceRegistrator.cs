using Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;
using Jellyfin.Plugin.ForcedSubtitles.Scanning;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ForcedSubtitles;

/// <summary>
/// Registers the plugin's shared services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ActivityLog>();
        serviceCollection.AddSingleton<CredentialsProvider>();
        serviceCollection.AddSingleton<OpenSubtitlesClient>();
        serviceCollection.AddSingleton<SubtitleInspector>();
        serviceCollection.AddSingleton<ScanStore>();
        serviceCollection.AddSingleton<SubtitleSaver>();
        serviceCollection.AddSingleton<ForcedSubtitleProcessor>();
    }
}
