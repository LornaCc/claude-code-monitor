using CCMonitor.Core.HookEvents;
using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class ClearSessionTerminalBindingMigrator
{
    private static readonly TimeSpan MaxHandoffAge = TimeSpan.FromMinutes(2);

    private readonly ClaudeSessionStateStore _stateStore;
    private readonly ManualTerminalBindingStore _bindingStore;
    private readonly TerminalBridgeRegistry _bridgeRegistry;

    public ClearSessionTerminalBindingMigrator(CcMonitorPaths paths)
    {
        _stateStore = new ClaudeSessionStateStore(paths);
        _bindingStore = new ManualTerminalBindingStore(paths);
        _bridgeRegistry = new TerminalBridgeRegistry(paths);
    }

    public async Task<ClearSessionBindingMigration?> TryMigrateAsync(
        HookEvent hookEvent,
        DateTimeOffset? now = null)
    {
        if (hookEvent.Kind != HookEventKind.SessionStart
            || !string.Equals(
                hookEvent.SessionStartSource,
                "clear",
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(hookEvent.SessionId))
        {
            return null;
        }

        var normalizedDirectory = TerminalBridgeRegistry.NormalizePath(
            hookEvent.WorkingDirectory);
        if (normalizedDirectory.Length == 0
            || _bindingStore.TryLoad(hookEvent.SessionId) is not null)
        {
            return null;
        }

        ClearSessionBindingMigration? migration = null;
        await _stateStore.WithTerminalIdentityLockAsync(
            $"clear-handoff:{normalizedDirectory}",
            async () =>
            {
                migration = await TryMigrateUnderLockAsync(
                    hookEvent,
                    normalizedDirectory,
                    now ?? DateTimeOffset.Now);
            });
        return migration;
    }

    private async Task<ClearSessionBindingMigration?> TryMigrateUnderLockAsync(
        HookEvent hookEvent,
        string normalizedDirectory,
        DateTimeOffset timestamp)
    {
        if (_bindingStore.TryLoad(hookEvent.SessionId) is not null)
        {
            return null;
        }

        var cutoff = timestamp - MaxHandoffAge;
        var candidates = (await _stateStore.LoadAllAsync())
            .Where(state =>
                !string.Equals(
                    state.SessionId,
                    hookEvent.SessionId,
                    StringComparison.OrdinalIgnoreCase)
                && state.Status == ClaudeSessionStatus.Closed
                && state.UpdatedAt >= cutoff
                && string.Equals(
                    state.LastHookEvent,
                    HookEventKind.SessionEnd.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                && TerminalBridgeRegistry.NormalizePath(state.WorkingDirectory)
                    == normalizedDirectory)
            .Select(state => new ClearBindingCandidate(
                state,
                _bindingStore.TryLoad(state.SessionId)))
            .Where(candidate =>
                candidate.Binding is not null
                && (!string.IsNullOrWhiteSpace(candidate.Binding.TerminalToken)
                    || candidate.Binding.TerminalProcessId is > 0))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var selected = SelectCandidate(candidates, hookEvent);
        if (selected?.Binding is null)
        {
            return null;
        }

        var migratedBinding = selected.Binding with
        {
            SessionId = hookEvent.SessionId,
            WorkingDirectory = string.IsNullOrWhiteSpace(hookEvent.WorkingDirectory)
                ? selected.Binding.WorkingDirectory
                : hookEvent.WorkingDirectory!,
            UpdatedAtUtc = timestamp.ToUniversalTime()
        };
        _bindingStore.Save(migratedBinding);

        await _stateStore.WithSessionLockAsync(
            selected.State.SessionId,
            async () =>
            {
                var oldState = await _stateStore.TryLoadAsync(selected.State.SessionId);
                if (oldState is null)
                {
                    return;
                }

                oldState.SupersededBySessionId = hookEvent.SessionId;
                await _stateStore.SaveAtomicAsync(oldState);
            });
        _bindingStore.Delete(selected.State.SessionId);

        return new ClearSessionBindingMigration(
            selected.State.SessionId,
            migratedBinding);
    }

    private ClearBindingCandidate? SelectCandidate(
        IReadOnlyList<ClearBindingCandidate> candidates,
        HookEvent hookEvent)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var liveBridges = _bridgeRegistry.LoadLive();
        var activeCandidates = candidates
            .Where(candidate =>
            {
                var binding = candidate.Binding!;
                var selection = _bridgeRegistry.Select(
                    liveBridges,
                    binding.TerminalToken,
                    hookEvent.WorkingDirectory ?? "",
                    ProjectNameResolver.FromWorkingDirectory(hookEvent.WorkingDirectory),
                    binding,
                    binding.TerminalProcessId);
                return selection.Bridge is not null
                    && selection.Bridge.WindowFocused
                    && binding.TerminalProcessId is > 0
                    && selection.Bridge.ActiveTerminalProcessId
                        == binding.TerminalProcessId;
            })
            .ToList();

        return activeCandidates.Count == 1
            ? activeCandidates[0]
            : null;
    }

    private sealed record ClearBindingCandidate(
        ClaudeSessionState State,
        ManualTerminalBinding? Binding);
}

public sealed record ClearSessionBindingMigration(
    string PreviousSessionId,
    ManualTerminalBinding Binding);
