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

    public void Save(ManualTerminalBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.SessionId))
        {
            throw new ArgumentException("A terminal binding requires a session id.", nameof(binding));
        }

        _paths.EnsureDirectories();
        var path = GetPath(binding.SessionId);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(binding, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    public ManualTerminalBinding? PreserveForClear(
        ClaudeSessionState state,
        DateTimeOffset? now = null)
    {
        var existing = TryLoad(state.SessionId);
        if (existing is not null)
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(state.TerminalToken)
            && state.TerminalProcessId is not > 0)
        {
            return null;
        }

        var binding = new ManualTerminalBinding(
            state.SessionId,
            state.TerminalToken,
            state.TerminalProcessId,
            "",
            state.WorkingDirectory,
            (now ?? DateTimeOffset.Now).ToUniversalTime());
        Save(binding);
        return binding;
    }

    public void Delete(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var path = GetPath(sessionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string sessionId)
        => Path.Combine(
            _paths.TerminalBindingsDirectory,
            $"{SanitizeFileName(sessionId)}.json");

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
