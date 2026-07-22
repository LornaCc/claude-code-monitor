using CCMonitor.Core.HookEvents;
using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class TerminalIdentityResolver
{
    private readonly TerminalBridgeRegistry _registry;
    private readonly ClaudeSessionStateStore _stateStore;

    public TerminalIdentityResolver(CcMonitorPaths paths)
    {
        _registry = new TerminalBridgeRegistry(paths);
        _stateStore = new ClaudeSessionStateStore(paths);
    }

    public TerminalIdentityResolution Resolve(HookEvent hookEvent)
        => Resolve(
            _registry.LoadLive(),
            hookEvent.TerminalToken,
            hookEvent.WorkingDirectory);

    public async Task<TerminalIdentityResolution> ResolveAsync(HookEvent hookEvent)
    {
        var liveBridges = _registry.LoadLive();
        var direct = Resolve(
            liveBridges,
            hookEvent.TerminalToken,
            hookEvent.WorkingDirectory);
        if (direct.HasIdentity || !string.IsNullOrWhiteSpace(hookEvent.TerminalToken))
        {
            return direct;
        }

        var normalizedDirectory = TerminalBridgeRegistry.NormalizePath(hookEvent.WorkingDirectory);
        if (normalizedDirectory.Length == 0)
        {
            return direct;
        }

        var exactTerminalProcessIds = liveBridges
            .SelectMany(bridge => bridge.Terminals ?? [])
            .Where(terminal =>
                terminal.ProcessId is > 0
                && TerminalBridgeRegistry.NormalizePath(terminal.WorkingDirectory) == normalizedDirectory)
            .Select(terminal => terminal.ProcessId!.Value)
            .Distinct()
            .ToList();
        if (exactTerminalProcessIds.Count < 2)
        {
            return direct;
        }

        var claimedByOtherSessions = (await _stateStore.LoadAllAsync())
            .Where(state =>
                !string.Equals(state.SessionId, hookEvent.SessionId, StringComparison.OrdinalIgnoreCase)
                && state.Status != ClaudeSessionStatus.Closed
                && TerminalBridgeRegistry.NormalizePath(state.WorkingDirectory) == normalizedDirectory
                && state.TerminalProcessId is > 0)
            .Select(state => state.TerminalProcessId!.Value)
            .ToHashSet();
        var unclaimed = exactTerminalProcessIds
            .Where(processId => !claimedByOtherSessions.Contains(processId))
            .ToList();
        return unclaimed.Count == 1
            ? new TerminalIdentityResolution(
                "",
                unclaimed[0],
                "uniqueUnclaimedExactWorkingDirectory",
                "Resolved the only exact-cwd terminal not owned by another session.")
            : direct;
    }

    internal static TerminalIdentityResolution Resolve(
        IReadOnlyList<TerminalBridgeRegistration> liveBridges,
        string? terminalToken,
        string? workingDirectory)
    {
        var normalizedToken = NormalizeToken(terminalToken);
        var terminals = liveBridges
            .SelectMany(bridge => bridge.Terminals ?? [])
            .ToList();

        if (normalizedToken.Length > 0)
        {
            var tokenMatches = terminals
                .Where(terminal => NormalizeToken(terminal.TerminalToken) == normalizedToken)
                .Select(terminal => terminal.ProcessId)
                .Where(processId => processId is > 0)
                .Distinct()
                .ToList();
            return tokenMatches.Count switch
            {
                1 => new TerminalIdentityResolution(
                    terminalToken!.Trim(),
                    tokenMatches[0],
                    "terminalToken",
                    "Resolved one registered terminal by token."),
                0 => new TerminalIdentityResolution(
                    terminalToken!.Trim(),
                    null,
                    "terminalTokenOnly",
                    "Kept the hook terminal token; no live registered process matched it."),
                _ => new TerminalIdentityResolution(
                    terminalToken!.Trim(),
                    null,
                    "ambiguousTerminalToken",
                    "Multiple live terminal processes registered the hook token.")
            };
        }

        var normalizedDirectory = TerminalBridgeRegistry.NormalizePath(workingDirectory);
        if (normalizedDirectory.Length == 0)
        {
            return TerminalIdentityResolution.None("The hook did not provide a working directory.");
        }

        var cwdMatches = terminals
            .Where(terminal =>
                TerminalBridgeRegistry.NormalizePath(terminal.WorkingDirectory) == normalizedDirectory
                && terminal.ProcessId is > 0)
            .Select(terminal => terminal.ProcessId!.Value)
            .Distinct()
            .ToList();
        return cwdMatches.Count switch
        {
            1 => new TerminalIdentityResolution(
                "",
                cwdMatches[0],
                "uniqueExactWorkingDirectory",
                "Resolved one live terminal process by exact working directory."),
            0 => TerminalIdentityResolution.None(
                "No live terminal had the hook working directory."),
            _ => TerminalIdentityResolution.None(
                "Multiple live terminals had the hook working directory.")
        };
    }

    private static string NormalizeToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
}

public sealed record TerminalIdentityResolution(
    string TerminalToken,
    int? TerminalProcessId,
    string MatchKind,
    string Reason)
{
    public bool HasIdentity
        => !string.IsNullOrWhiteSpace(TerminalToken) || TerminalProcessId is > 0;

    public bool CanSupersedeSessions
        => !string.IsNullOrWhiteSpace(TerminalToken);

    public string LockKey => !string.IsNullOrWhiteSpace(TerminalToken)
        ? $"token:{TerminalToken.Trim().ToLowerInvariant()}"
        : $"pid:{TerminalProcessId}";

    public static TerminalIdentityResolution None(string reason)
        => new("", null, "noMatch", reason);
}
