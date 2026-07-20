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
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _requestPath;
    private readonly string _resultPath;
    private readonly string _bindingRequestPath;
    private readonly TerminalBridgeRegistry _registry;
    private readonly ManualTerminalBindingStore _bindingStore;

    public VsCodeTerminalBridge(CcMonitorPaths paths)
    {
        _requestPath = Path.Combine(paths.RootDirectory, "focus-terminal.json");
        _resultPath = Path.Combine(paths.RootDirectory, "focus-terminal-result.json");
        _bindingRequestPath = Path.Combine(paths.RootDirectory, "bind-terminal-session.json");
        _registry = new TerminalBridgeRegistry(paths);
        _bindingStore = new ManualTerminalBindingStore(paths);
    }

    public async Task<TerminalFocusResult> RequestFocusAsync(
        string sessionId,
        string terminalToken,
        int? terminalProcessId,
        string workingDirectory,
        string projectName,
        TimeSpan? timeout = null)
    {
        var liveBridges = _registry.LoadLive();
        var manualBinding = _bindingStore.TryLoad(sessionId);
        var selection = _registry.Select(
            liveBridges,
            terminalToken,
            workingDirectory,
            projectName,
            manualBinding,
            terminalProcessId);
        if (!selection.IsMatch)
        {
            return new TerminalFocusResult(
                "",
                DateTimeOffset.UtcNow,
                selection.MatchKind == "bridgeNotRunning"
                    ? TerminalFocusStatus.BridgeNotRunning
                    : TerminalFocusStatus.NoMatch,
                null,
                "",
                "",
                selection.MatchKind,
                selection.Reason,
                "",
                selection.LiveBridgeCount);
        }

        var request = new TerminalFocusRequest(
            selection.Bridge!.ProtocolVersion >= 3 ? 3 : 2,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            selection.Bridge.BridgeId,
            sessionId,
            manualBinding?.TerminalToken ?? terminalToken,
            manualBinding?.TerminalProcessId
                ?? (selection.MatchKind == "terminalProcessId" ? terminalProcessId : null),
            workingDirectory,
            projectName);

        WriteAtomic(_requestPath, request);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMilliseconds(1500));
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(60);
            var result = TryReadResult();
            if (result?.RequestId == request.RequestId)
            {
                return result with
                {
                    LiveBridgeCount = selection.LiveBridgeCount
                };
            }
        }

        return new TerminalFocusResult(
            request.RequestId,
            DateTimeOffset.UtcNow,
            TerminalFocusStatus.NoMatch,
            null,
            "",
            selection.Bridge.WorkspaceName,
            "bridgeTimeout",
            $"Bridge {selection.Bridge.BridgeId} did not answer within the focus timeout.",
            selection.Bridge.BridgeId,
            selection.LiveBridgeCount);
    }

    public void PrepareManualBinding(
        string sessionId,
        string workingDirectory,
        string projectName)
    {
        WriteAtomic(
            _bindingRequestPath,
            new TerminalBindingRequest(
                3,
                sessionId,
                workingDirectory,
                projectName,
                DateTimeOffset.UtcNow));
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
        int ProtocolVersion,
        string RequestId,
        DateTimeOffset RequestedAtUtc,
        string TargetBridgeId,
        string SessionId,
        string TerminalToken,
        int? TerminalProcessId,
        string WorkingDirectory,
        string ProjectName);

    private sealed record TerminalBindingRequest(
        int ProtocolVersion,
        string SessionId,
        string WorkingDirectory,
        string ProjectName,
        DateTimeOffset RequestedAtUtc);
}

public enum TerminalFocusStatus
{
    Matched,
    NoMatch,
    BridgeNotRunning
}

public sealed record TerminalFocusResult(
    string RequestId,
    DateTimeOffset CompletedAtUtc,
    TerminalFocusStatus Status,
    int? TerminalProcessId,
    string TerminalName,
    string WorkspaceName,
    string MatchKind,
    string Reason,
    string BridgeId,
    int LiveBridgeCount = 0,
    bool? WindowFocused = null,
    string WindowFocusReason = "");
