using System.Text;
using System.Text.Json;
using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class SessionUsageMetricsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly CcMonitorPaths _paths;

    public SessionUsageMetricsStore(CcMonitorPaths paths)
    {
        _paths = paths;
    }

    public string GetMetricsPath(string sessionId) => Path.Combine(_paths.UsageMetricsDirectory, $"{SanitizeFileName(sessionId)}.json");

    public async Task SaveAtomicAsync(SessionUsageMetrics metrics)
    {
        _paths.EnsureDirectories();
        var path = GetMetricsPath(metrics.SessionId);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(metrics, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    public IReadOnlyDictionary<string, SessionUsageMetrics> LoadAll()
    {
        _paths.EnsureDirectories();
        var metrics = new Dictionary<string, SessionUsageMetrics>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_paths.UsageMetricsDirectory, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<SessionUsageMetrics>(File.ReadAllText(path), JsonOptions);
                if (item is not null && !string.IsNullOrWhiteSpace(item.SessionId))
                {
                    metrics[item.SessionId] = item;
                }
            }
            catch
            {
                // Ignore malformed metrics. They are advisory, not lifecycle state.
            }
        }

        return metrics;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}
