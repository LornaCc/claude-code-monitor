using System.Text.Json;
using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class MonitorConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly CcMonitorPaths _paths;

    public MonitorConfigStore(CcMonitorPaths paths)
    {
        _paths = paths;
    }

    public MonitorConfig LoadOrCreate()
    {
        _paths.EnsureDirectories();
        if (!File.Exists(_paths.ConfigPath))
        {
            var defaults = new MonitorConfig();
            Save(defaults);
            return defaults;
        }

        try
        {
            var config = JsonSerializer.Deserialize<MonitorConfig>(File.ReadAllText(_paths.ConfigPath), JsonOptions) ?? new MonitorConfig();
            config.SessionNames ??= new Dictionary<string, string>();
            return config;
        }
        catch
        {
            var corruptPath = $"{_paths.ConfigPath}.corrupted.{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(_paths.ConfigPath, corruptPath, overwrite: true);
            var defaults = new MonitorConfig();
            Save(defaults);
            return defaults;
        }
    }

    public void Save(MonitorConfig config)
    {
        _paths.EnsureDirectories();
        File.WriteAllText(_paths.ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }
}
