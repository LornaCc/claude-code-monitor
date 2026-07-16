using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CCMonitor.Core.Services;

namespace CCMonitor.App;

public sealed class VsCodeTerminalBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _requestPath;
    private readonly string _resultPath;

    public VsCodeTerminalBridge(CcMonitorPaths paths)
    {
        _requestPath = Path.Combine(paths.RootDirectory, "focus-terminal.json");
        _resultPath = Path.Combine(paths.RootDirectory, "focus-terminal-result.json");
    }

    public async Task<TerminalFocusResult?> RequestFocusAsync(
        string sessionId,
        int? terminalProcessId,
        string workingDirectory,
        string projectName,
        TimeSpan? timeout = null)
    {
        var request = new TerminalFocusRequest(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            sessionId,
            terminalProcessId,
            workingDirectory,
            projectName);

        WriteAtomic(_requestPath, request);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMilliseconds(1200));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(60);
            var result = TryReadResult();
            if (result?.RequestId == request.RequestId) return result;
        }

        return null;
    }

    private TerminalFocusResult? TryReadResult()
    {
        try
        {
            if (!File.Exists(_resultPath)) return null;
            using var stream = new FileStream(_resultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<TerminalFocusResult>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private sealed record TerminalFocusRequest(
        string RequestId,
        DateTimeOffset RequestedAtUtc,
        string SessionId,
        int? TerminalProcessId,
        string WorkingDirectory,
        string ProjectName);
}

public sealed record TerminalFocusResult(
    string RequestId,
    DateTimeOffset CompletedAtUtc,
    int? TerminalProcessId,
    string TerminalName,
    string WorkspaceName,
    string MatchKind);
