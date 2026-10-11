using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ForcedSubtitles.Scanning;

/// <summary>Outcome of checking one video.</summary>
public enum ScanStatus
{
    /// <summary>The video already had the subtitles it needs.</summary>
    AlreadyHas,

    /// <summary>A subtitle was downloaded.</summary>
    Downloaded,

    /// <summary>OpenSubtitles had nothing suitable.</summary>
    NotFound,

    /// <summary>Something went wrong; retried next run.</summary>
    Error
}

/// <summary>Result of the last check of one video.</summary>
public sealed class ScanRecord
{
    /// <summary>Gets or sets the item name for display.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets when the item was last checked (UTC).</summary>
    public DateTime LastChecked { get; set; }

    /// <summary>Gets or sets the outcome.</summary>
    public ScanStatus Status { get; set; }

    /// <summary>Gets or sets a human readable detail.</summary>
    public string? Detail { get; set; }

    /// <summary>Gets or sets the subtitle file written, for <see cref="ScanStatus.Downloaded"/>.</summary>
    public string? SavedPath { get; set; }
}

/// <summary>
/// Remembers what has been checked so unchanged videos aren't searched again every run.
/// Persisted as JSON in the plugin's data folder.
/// </summary>
public sealed class ScanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILogger<ScanStore> _logger;
    private Dictionary<Guid, ScanRecord>? _records;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanStore"/> class.
    /// </summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public ScanStore(IApplicationPaths paths, ILogger<ScanStore> logger)
    {
        _path = Path.Combine(paths.PluginConfigurationsPath, "ForcedSubtitles", "scan-state.json");
        _logger = logger;
    }

    /// <summary>Gets the record for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The record, or null if never checked.</returns>
    public ScanRecord? Get(Guid itemId)
    {
        lock (_lock)
        {
            return Load().GetValueOrDefault(itemId);
        }
    }

    /// <summary>Stores a record (call <see cref="Save"/> to persist).</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="record">The record.</param>
    public void Set(Guid itemId, ScanRecord record)
    {
        lock (_lock)
        {
            Load()[itemId] = record;
        }
    }

    /// <summary>Returns a snapshot of all records.</summary>
    /// <returns>All records.</returns>
    public List<KeyValuePair<Guid, ScanRecord>> Snapshot()
    {
        lock (_lock)
        {
            return Load().ToList();
        }
    }

    /// <summary>Removes records whose item no longer exists.</summary>
    /// <param name="exists">Returns whether an item id still exists.</param>
    /// <returns>How many records were removed.</returns>
    public int RemoveWhereMissing(Func<Guid, bool> exists)
    {
        lock (_lock)
        {
            var records = Load();
            var gone = records.Keys.Where(id => !exists(id)).ToList();
            foreach (var id in gone)
            {
                records.Remove(id);
            }

            return gone.Count;
        }
    }

    /// <summary>Forgets everything so the next run re-checks every video.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _records = [];
            Save();
        }
    }

    /// <summary>Writes the records to disk.</summary>
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Load(), JsonOptions));
                File.Move(tmp, _path, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save forced subtitle scan state to {Path}", _path);
            }
        }
    }

    private Dictionary<Guid, ScanRecord> Load()
    {
        if (_records is not null)
        {
            return _records;
        }

        try
        {
            if (File.Exists(_path))
            {
                _records = JsonSerializer.Deserialize<Dictionary<Guid, ScanRecord>>(File.ReadAllText(_path), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable scan state at {Path}", _path);
        }

        return _records ??= [];
    }
}
