using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;

/// <summary>Resolved OpenSubtitles credentials and where each came from.</summary>
/// <param name="ApiKey">API key, or empty.</param>
/// <param name="ApiKeySource">"settings", "built-in" or "none".</param>
/// <param name="Username">Username, or empty.</param>
/// <param name="Password">Password, or empty.</param>
/// <param name="LoginSource">"settings", "OpenSubtitles plugin" or "none".</param>
public sealed record OpenSubtitlesCredentials(string ApiKey, string ApiKeySource, string Username, string Password, string LoginSource)
{
    /// <summary>Gets a value indicating whether a username and password are available.</summary>
    public bool HasLogin => Username.Length > 0 && Password.Length > 0;
}

/// <summary>
/// Works out which API key and login to use, so the settings page can be left blank:
/// <list type="bullet">
/// <item>API key: the settings page, else the key built into this plugin at compile time.</item>
/// <item>Login: the settings page, else the official OpenSubtitles plugin's saved login.</item>
/// </list>
/// </summary>
public sealed class CredentialsProvider
{
    // Configuration file of the official Jellyfin OpenSubtitles plugin.
    private const string OfficialPluginConfigFile = "Jellyfin.Plugin.OpenSubtitles.xml";

    private static readonly string BuiltInApiKey = typeof(CredentialsProvider).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "OpenSubtitlesApiKey")?.Value?.Trim() ?? string.Empty;

    private readonly string _officialConfigPath;
    private readonly ILogger<CredentialsProvider> _logger;
    private (DateTime Stamp, string User, string Pass)? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="CredentialsProvider"/> class.
    /// </summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public CredentialsProvider(IApplicationPaths paths, ILogger<CredentialsProvider> logger)
    {
        _officialConfigPath = Path.Combine(paths.PluginConfigurationsPath, OfficialPluginConfigFile);
        _logger = logger;
    }

    /// <summary>Resolves the credentials to use right now.</summary>
    /// <returns>The credentials.</returns>
    public OpenSubtitlesCredentials Get()
    {
        var config = Plugin.Instance?.Configuration;

        string apiKey = config?.ApiKey?.Trim() ?? string.Empty;
        string apiKeySource = "settings";
        if (apiKey.Length == 0)
        {
            apiKey = BuiltInApiKey;
            apiKeySource = apiKey.Length > 0 ? "built-in" : "none";
        }

        string user = config?.Username?.Trim() ?? string.Empty;
        string pass = config?.Password ?? string.Empty;
        string loginSource = "settings";
        if (user.Length == 0 || pass.Length == 0)
        {
            (user, pass) = ReadOfficialPluginLogin();
            loginSource = user.Length > 0 && pass.Length > 0 ? "OpenSubtitles plugin" : "none";
        }

        return new OpenSubtitlesCredentials(apiKey, apiKeySource, user, pass, loginSource);
    }

    private (string User, string Pass) ReadOfficialPluginLogin()
    {
        try
        {
            if (!File.Exists(_officialConfigPath))
            {
                return (string.Empty, string.Empty);
            }

            // Called for every API request, so only re-read the file when it changes.
            var stamp = File.GetLastWriteTimeUtc(_officialConfigPath);
            var cached = _cache;
            if (cached is not null && cached.Value.Stamp == stamp)
            {
                return (cached.Value.User, cached.Value.Pass);
            }

            var root = XDocument.Load(_officialConfigPath).Root;
            var user = root?.Element("Username")?.Value.Trim() ?? string.Empty;
            var pass = root?.Element("Password")?.Value ?? string.Empty;
            _cache = (stamp, user, pass);
            return (user, pass);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read {Path}", _officialConfigPath);
            return (string.Empty, string.Empty);
        }
    }
}
