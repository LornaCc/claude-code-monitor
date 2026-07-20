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
    public async Task New_session_closes_previous_session_on_the_same_terminal()
    {
        var store = new ClaudeSessionStateStore(_paths);
        var oldState = State("old-session", ClaudeSessionStatus.Interrupted, 26100);
        var unrelated = State("other-session", ClaudeSessionStatus.Running, 30000);
        var current = State("new-session", ClaudeSessionStatus.Idle, 26100);
        await store.SaveAtomicAsync(oldState);
        await store.SaveAtomicAsync(unrelated);
        await store.SaveAtomicAsync(current);

        var closed = await new TerminalSessionReconciler(store)
            .CloseSupersededSessionsAsync(current);
        var states = await store.LoadAllAsync();

        Assert.Equal(["old-session"], closed);
        var superseded = Assert.Single(states, state => state.SessionId == "old-session");
        Assert.Equal(ClaudeSessionStatus.Closed, superseded.Status);
        Assert.Equal("new-session", superseded.SupersededBySessionId);
        Assert.Equal(
            ClaudeSessionStatus.Running,
            Assert.Single(states, state => state.SessionId == "other-session").Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteBridge(string bridgeId, TerminalBridgeTerminal terminal)
    {
        var registration = new TerminalBridgeRegistration(
            3,
            bridgeId,
            123,
            DateTimeOffset.UtcNow,
            "",
            [],
            [terminal]);
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
