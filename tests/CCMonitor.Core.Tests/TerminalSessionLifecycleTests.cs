using System.Text.Json;
using CCMonitor.Core.HookEvents;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;
using Xunit;

namespace CCMonitor.Core.Tests;

public sealed class TerminalSessionLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"cc-monitor-terminal-lifecycle-{Guid.NewGuid():N}");
    private readonly CcMonitorPaths _paths;

    public TerminalSessionLifecycleTests()
    {
        _paths = new CcMonitorPaths(_root);
        _paths.EnsureDirectories();
    }

    [Fact]
    public void Identity_resolver_uses_a_unique_exact_cwd_terminal()
    {
        WriteBridge(
            "one",
            new TerminalBridgeTerminal(
                "terminal-one",
                "",
                "PowerShell",
                26100,
                @"C:\Users\admin"));

        var result = new TerminalIdentityResolver(_paths).Resolve(new HookEvent
        {
            SessionId = "new-session",
            WorkingDirectory = @"C:\Users\admin"
        });

        Assert.True(result.HasIdentity);
        Assert.Equal(26100, result.TerminalProcessId);
        Assert.Equal("uniqueExactWorkingDirectory", result.MatchKind);
    }

    [Fact]
    public void Identity_resolver_rejects_an_ambiguous_cwd()
    {
        WriteBridge(
            "one",
            new TerminalBridgeTerminal(
                "terminal-one",
                "",
                "PowerShell",
                26100,
                @"C:\Users\admin"));
        WriteBridge(
            "two",
            new TerminalBridgeTerminal(
                "terminal-two",
                "",
                "PowerShell",
                26101,
                @"C:\Users\admin"));

        var result = new TerminalIdentityResolver(_paths).Resolve(new HookEvent
        {
            SessionId = "new-session",
            WorkingDirectory = @"C:\Users\admin"
        });

        Assert.False(result.HasIdentity);
        Assert.Equal("noMatch", result.MatchKind);
    }

    [Fact]
    public async Task Identity_resolver_uses_the_only_terminal_not_claimed_by_another_session()
    {
        WriteBridge(
            "one",
            new TerminalBridgeTerminal(
                "terminal-one",
                "",
                "PowerShell one",
                26100,
                @"C:\Users\admin"),
            new TerminalBridgeTerminal(
                "terminal-two",
                "",
                "PowerShell two",
                26101,
                @"C:\Users\admin"));
        var store = new ClaudeSessionStateStore(_paths);
        await store.SaveAtomicAsync(State("new-after-clear", ClaudeSessionStatus.Done, 26100));

        var result = await new TerminalIdentityResolver(_paths).ResolveAsync(new HookEvent
        {
            SessionId = "resumed-old-session",
            WorkingDirectory = @"C:\Users\admin"
        });

        Assert.True(result.HasIdentity);
        Assert.Equal(26101, result.TerminalProcessId);
        Assert.Equal("uniqueUnclaimedExactWorkingDirectory", result.MatchKind);
    }

    [Fact]
    public async Task Identity_resolver_does_not_guess_between_multiple_unclaimed_terminals()
    {
        WriteBridge(
            "one",
            new TerminalBridgeTerminal(
                "terminal-one",
                "",
                "PowerShell one",
                26100,
                @"C:\Users\admin"),
            new TerminalBridgeTerminal(
                "terminal-two",
                "",
                "PowerShell two",
                26101,
                @"C:\Users\admin"));

        var result = await new TerminalIdentityResolver(_paths).ResolveAsync(new HookEvent
        {
            SessionId = "session-without-owner",
            WorkingDirectory = @"C:\Users\admin"
        });

        Assert.False(result.HasIdentity);
        Assert.Equal("noMatch", result.MatchKind);
    }

    [Fact]
    public async Task New_session_closes_previous_session_on_the_same_terminal()
    {
        var store = new ClaudeSessionStateStore(_paths);
        var oldState = State("old-session", ClaudeSessionStatus.Interrupted, 26100);
        var unrelated = State("other-session", ClaudeSessionStatus.Running, 30000);
        var current = State("new-session", ClaudeSessionStatus.Idle, 26100);
        oldState.TerminalToken = "managed-terminal-token";
        unrelated.TerminalToken = "other-terminal-token";
        current.TerminalToken = "managed-terminal-token";
        await store.SaveAtomicAsync(oldState);
        await store.SaveAtomicAsync(unrelated);
        await store.SaveAtomicAsync(current);

        var closed = await new TerminalSessionReconciler(store)
            .CloseSupersededSessionsAsync(
                current,
                new TerminalIdentityResolution(
                    "managed-terminal-token",
                    26100,
                    "terminalToken",
                    "Resolved one registered terminal by token."));
        var states = await store.LoadAllAsync();

        Assert.Equal(["old-session"], closed);
        var superseded = Assert.Single(states, state => state.SessionId == "old-session");
        Assert.Equal(ClaudeSessionStatus.Closed, superseded.Status);
        Assert.Equal("new-session", superseded.SupersededBySessionId);
        Assert.Equal(
            ClaudeSessionStatus.Running,
            Assert.Single(states, state => state.SessionId == "other-session").Status);
    }

    [Fact]
    public async Task Cwd_inferred_terminal_does_not_close_another_session()
    {
        var store = new ClaudeSessionStateStore(_paths);
        var oldState = State("old-session", ClaudeSessionStatus.Running, 26100);
        var current = State("new-session", ClaudeSessionStatus.Idle, 26100);
        await store.SaveAtomicAsync(oldState);
        await store.SaveAtomicAsync(current);

        var closed = await new TerminalSessionReconciler(store)
            .CloseSupersededSessionsAsync(
                current,
                new TerminalIdentityResolution(
                    "",
                    26100,
                    "uniqueExactWorkingDirectory",
                    "Resolved one live terminal process by exact working directory."));
        var states = await store.LoadAllAsync();

        Assert.Empty(closed);
        var preserved = Assert.Single(states, state => state.SessionId == "old-session");
        Assert.Equal(ClaudeSessionStatus.Running, preserved.Status);
        Assert.Null(preserved.SupersededBySessionId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteBridge(string bridgeId, params TerminalBridgeTerminal[] terminals)
    {
        var registration = new TerminalBridgeRegistration(
            3,
            bridgeId,
            123,
            DateTimeOffset.UtcNow,
            "",
            [],
            terminals);
        File.WriteAllText(
            Path.Combine(_paths.TerminalBridgesDirectory, $"{bridgeId}.json"),
            JsonSerializer.Serialize(registration));
    }

    private static ClaudeSessionState State(
        string sessionId,
        ClaudeSessionStatus status,
        int terminalProcessId)
        => new()
        {
            SessionId = sessionId,
            WorkingDirectory = @"C:\Users\admin",
            ProjectName = "admin",
            TerminalProcessId = terminalProcessId,
            Status = status,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
}
