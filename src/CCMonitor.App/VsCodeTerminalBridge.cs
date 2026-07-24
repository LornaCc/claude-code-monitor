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
    private readonly ClaudeSessionStateStore _stateStore;

    public VsCodeTerminalBridge(CcMonitorPaths paths)
    {
        _requestPath = Path.Combine(paths.RootDirectory, "focus-terminal.json");
        _resultPath = Path.Combine(paths.RootDirectory, "focus-terminal-result.json");
        _bindingRequestPath = Path.Combine(paths.RootDirectory, "bind-terminal-session.json");
        _registry = new TerminalBridgeRegistry(paths);
        _bindingStore = new ManualTerminalBindingStore(paths);
        _stateStore = new ClaudeSessionStateStore(paths);
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
        var preferredTerminalProcessId = terminalProcessId;
        if (manualBinding is null
            && string.IsNullOrWhiteSpace(terminalToken)
            && terminalProcessId is > 0)
        {
            var normalizedDirectory = TerminalBridgeRegistry.NormalizePath(workingDirectory);
            var hasConflictingOwner = normalizedDirectory.Length > 0
                && (await _stateStore.LoadAllAsync()).Any(state =>
                !string.Equals(state.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)
                && state.Status != CCMonitor.Core.Models.ClaudeSessionStatus.Closed
                && state.TerminalProcessId == terminalProcessId
                && TerminalBridgeRegistry.NormalizePath(state.WorkingDirectory) == normalizedDirectory);
            if (hasConflictingOwner)
            {
                preferredTerminalProcessId = null;
            }
        }

        var selection = _registry.Select(
            liveBridges,
            terminalToken,
            workingDirectory,
            projectName,
            manualBinding,
            preferredTerminalProcessId);
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
                ?? (selection.MatchKind is "terminalProcessId" or "terminalProcessIdActiveTerminal"
                    ? preferredTerminalProcessId
                    : null),
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
                var completed = result with
                {
                    LiveBridgeCount = selection.LiveBridgeCount
                };
                var refreshedBinding = CreateRefreshedBinding(
                    manualBinding,
                    completed,
                    selection.Bridge.BridgeId,
                    workingDirectory,
                    DateTimeOffset.UtcNow);
                if (refreshedBinding is null)
                {
                    return completed;
                }

                try
                {
                    _bindingStore.Save(refreshedBinding);
                    return completed with
                    {
                        BindingRefresh = $"updated:{manualBinding!.BridgeId}->{refreshedBinding.BridgeId}"
                    };
                }
                catch (Exception exception)
                {
                    return completed with
                    {
                        BindingRefresh = $"failed:{exception.GetType().Name}"
                    };
                }
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

    internal static CCMonitor.Core.Models.ManualTerminalBinding? CreateRefreshedBinding(
        CCMonitor.Core.Models.ManualTerminalBinding? binding,
        TerminalFocusResult result,
        string selectedBridgeId,
        string workingDirectory,
        DateTimeOffset now)
    {
        if (binding is null
            || result.Status != TerminalFocusStatus.Matched
            || string.IsNullOrWhiteSpace(selectedBridgeId))
        {
            return null;
        }

        var terminalToken = string.IsNullOrWhiteSpace(result.TerminalToken)
            ? binding.TerminalToken
            : result.TerminalToken;
        var terminalProcessId = result.TerminalProcessId
            ?? binding.TerminalProcessId;
        var terminalName = string.IsNullOrWhiteSpace(result.TerminalName)
            ? binding.TerminalName
            : result.TerminalName;
        var effectiveDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? binding.WorkingDirectory
            : workingDirectory;
        var changed = !string.Equals(
                binding.BridgeId,
                selectedBridgeId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                binding.TerminalToken,
                terminalToken,
                StringComparison.OrdinalIgnoreCase)
            || binding.TerminalProcessId != terminalProcessId
            || !string.Equals(binding.TerminalName, terminalName, StringComparison.Ordinal)
            || !string.Equals(
                TerminalBridgeRegistry.NormalizePath(binding.WorkingDirectory),
                TerminalBridgeRegistry.NormalizePath(effectiveDirectory),
                StringComparison.OrdinalIgnoreCase);
        if (!changed)
        {
            return null;
        }

        return binding with
        {
            BridgeId = selectedBridgeId,
            TerminalToken = terminalToken,
            TerminalProcessId = terminalProcessId,
            TerminalName = terminalName,
            WorkingDirectory = effectiveDirectory,
            UpdatedAtUtc = now.ToUniversalTime()
        };
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
    string WindowFocusReason = "",
    string TerminalToken = "",
    string BindingRefresh = "");
