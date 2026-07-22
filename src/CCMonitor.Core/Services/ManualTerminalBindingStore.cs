using System.Text.Json;
using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class ManualTerminalBindingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CcMonitorPaths _paths;

    public ManualTerminalBindingStore(CcMonitorPaths paths)
    {
        _paths = paths;
    }

    public ManualTerminalBinding? TryLoad(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var path = Path.Combine(
            _paths.TerminalBindingsDirectory,
            $"{SanitizeFileName(sessionId)}.json");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var binding = JsonSerializer.Deserialize<ManualTerminalBinding>(stream, JsonOptions);
            return binding is not null
                && string.Equals(binding.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                ? binding
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Delete(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var path = Path.Combine(
            _paths.TerminalBindingsDirectory,
            $"{SanitizeFileName(sessionId)}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
