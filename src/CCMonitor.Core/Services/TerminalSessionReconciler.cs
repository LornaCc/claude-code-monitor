using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class TerminalSessionReconciler
{
    private readonly ClaudeSessionStateStore _store;

    public TerminalSessionReconciler(ClaudeSessionStateStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<string>> CloseSupersededSessionsAsync(
        ClaudeSessionState current,
        TerminalIdentityResolution terminalIdentity,
        DateTimeOffset? now = null)
    {
        if (!terminalIdentity.CanSupersedeSessions)
        {
            return [];
        }

        var superseded = new List<string>();
        var timestamp = now ?? DateTimeOffset.Now;
        var candidates = (await _store.LoadAllAsync())
            .Where(state =>
                !string.Equals(state.SessionId, current.SessionId, StringComparison.OrdinalIgnoreCase)
                && state.Status != ClaudeSessionStatus.Closed
                && IsSameTerminal(state, current))
            .ToList();

        foreach (var candidate in candidates)
        {
            await _store.WithSessionLockAsync(candidate.SessionId, async () =>
            {
                var latest = (await _store.LoadAllAsync())
                    .FirstOrDefault(state => string.Equals(
                        state.SessionId,
                        candidate.SessionId,
                        StringComparison.OrdinalIgnoreCase));
                if (latest is null
                    || latest.Status == ClaudeSessionStatus.Closed
                    || !IsSameTerminal(latest, current))
                {
                    return;
                }

                latest.Status = ClaudeSessionStatus.Closed;
                latest.UpdatedAt = timestamp;
                latest.SupersededBySessionId = current.SessionId;
                await _store.SaveAtomicAsync(latest);
                superseded.Add(latest.SessionId);
            });
        }

        return superseded;
    }

    private static bool IsSameTerminal(
        ClaudeSessionState left,
        ClaudeSessionState right)
    {
        if (!string.IsNullOrWhiteSpace(left.TerminalToken)
            && !string.IsNullOrWhiteSpace(right.TerminalToken)
            && string.Equals(
                left.TerminalToken.Trim(),
                right.TerminalToken.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return left.TerminalProcessId is > 0
            && right.TerminalProcessId is > 0
            && left.TerminalProcessId == right.TerminalProcessId;
    }
}
