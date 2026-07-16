using System.Text.Json;

namespace CCMonitor.Core.Services;

public sealed class SessionVisibilityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly CcMonitorPaths _paths;

    public SessionVisibilityStore(CcMonitorPaths paths)
    {
        _paths = paths;
    }

    public HashSet<string> LoadHidden()
    {
        _paths.EnsureDirectories();
        return LoadHashSet(_paths.HiddenSessionsPath);
    }

    public void SaveHidden(HashSet<string> hiddenSessionIds)
        => WriteAtomic(_paths.HiddenSessionsPath, hiddenSessionIds.OrderBy(x => x).ToArray());

    public void Hide(string sessionId)
    {
        var hidden = LoadHidden();
        hidden.Add(sessionId);
        SaveHidden(hidden);
    }

    public void Restore(string sessionId)
    {
        var hidden = LoadHidden();
        hidden.Remove(sessionId);
        SaveHidden(hidden);
    }

    public void RestoreAll()
        => SaveHidden(new HashSet<string>());

    public Dictionary<string, DateTimeOffset> LoadRemoved()
    {
        _paths.EnsureDirectories();
        try
        {
            if (!File.Exists(_paths.RemovedSessionsPath)) return new Dictionary<string, DateTimeOffset>();
            return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_paths.RemovedSessionsPath), JsonOptions)
                ?? new Dictionary<string, DateTimeOffset>();
        }
        catch
        {
            return new Dictionary<string, DateTimeOffset>();
        }
    }

    public bool IsRemoved(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var removed = LoadRemoved();
        PruneRemoved(removed);
        return removed.ContainsKey(sessionId);
    }

    public void RemovePermanently(string sessionId)
    {
        var removed = LoadRemoved();
        PruneRemoved(removed);
        removed[sessionId] = DateTimeOffset.Now;
        WriteAtomic(_paths.RemovedSessionsPath, removed);

        var hidden = LoadHidden();
        hidden.Remove(sessionId);
        SaveHidden(hidden);
    }

    private static HashSet<string> LoadHashSet(string path)
    {
        try
        {
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path), JsonOptions) ?? [];
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void PruneRemoved(Dictionary<string, DateTimeOffset> removed)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-30);
        foreach (var key in removed.Where(x => x.Value < cutoff).Select(x => x.Key).ToList())
        {
            removed.Remove(key);
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }
}
