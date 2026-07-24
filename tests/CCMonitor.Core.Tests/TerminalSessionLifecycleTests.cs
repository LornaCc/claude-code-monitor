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
                "bridge-token-one",
                "PowerShell",
                26100,
                @"C:\Users\admin"));

        var result = new TerminalIdentityResolver(_paths).Resolve(new HookEvent
        {
            SessionId = "new-session",
            WorkingDirectory = @"C:\Users\admin"
        });

        Assert.True(result.HasIdentity);
        Assert.Equal("bridge-token-one", result.TerminalToken);
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

    [Theory]
    [InlineData(HookEventKind.SessionStart)]
    [InlineData(HookEventKind.UserPromptSubmit)]
    public async Task Identity_resolver_claims_the_focused_active_terminal_for_user_events(
        HookEventKind eventKind)
    {
        WriteBridgeWithWindowState(
            "one",
            26101,
            true,
            new TerminalBridgeTerminal(
                "terminal-one",
                "",
                "PowerShell one",
                26100,
                @"C:\Users\admin"),
            new TerminalBridgeTerminal(
                "terminal-two",
                "active-terminal-token",
                "PowerShell two",
                26101,
                @"C:\Users\admin"));

        var result = await new TerminalIdentityResolver(_paths).ResolveAsync(new HookEvent
        {
            Kind = eventKind,
            SessionId = "session-in-active-terminal",
            WorkingDirectory = @"C:\Users\admin"
        });

        Assert.True(result.HasIdentity);
        Assert.Equal("active-terminal-token", result.TerminalToken);
        Assert.Equal(26101, result.TerminalProcessId);
        Assert.Equal("focusedActiveTerminal", result.MatchKind);
    }

    [Theory]
    [InlineData(HookEventKind.SessionStart)]
    [InlineData(HookEventKind.UserPromptSubmit)]
    public async Task Identity_resolver_claims_active_terminal_when_shell_cwd_is_missing(
        HookEventKind eventKind)
    {
        WriteBridgeWithWindowState(
            "one",
            26101,
            true,
            [@"D:\ws\starry1000_dev_rpt"],
            new TerminalBridgeTerminal(
                "terminal-one",
                "ordinary-terminal-token",
                "bash",
                26101,
                ""));

        var result = await new TerminalIdentityResolver(_paths).ResolveAsync(new HookEvent
        {
            Kind = eventKind,
            SessionId = "session-in-git-bash",
            WorkingDirectory = @"D:\ws\starry1000_dev_rpt"
        });

        Assert.True(result.HasIdentity);
        Assert.Equal("ordinary-terminal-token", result.TerminalToken);
        Assert.Equal(26101, result.TerminalProcessId);
        Assert.Equal("focusedActiveTerminal", result.MatchKind);
    }

    [Fact]
    public async Task Identity_resolver_does_not_claim_the_active_terminal_for_background_events()
    {
        WriteBridgeWithWindowState(
            "one",
            26101,
            true,
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
            Kind = HookEventKind.Activity,
            SessionId = "background-session",
            WorkingDirectory = @"C:\Users\admin"
        });

        Assert.False(result.HasIdentity);
        Assert.Equal("noMatch", result.MatchKind);
    }

    [Fact]
    public async Task Identity_resolver_does_not_guess_between_focused_active_terminals()
    {
        WriteBridgeWithWindowState(
            "one",
            26100,
            true,
            new TerminalBridgeTerminal(
                "terminal-one",
                "",
                "PowerShell one",
                26100,
                @"C:\Users\admin"));
        WriteBridgeWithWindowState(
            "two",
            26101,
            true,
            new TerminalBridgeTerminal(
                "terminal-two",
                "",
                "PowerShell two",
                26101,
                @"C:\Users\admin"));

        var result = await new TerminalIdentityResolver(_paths).ResolveAsync(new HookEvent
        {
            Kind = HookEventKind.UserPromptSubmit,
            SessionId = "ambiguous-session",
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

    [Fact]
    public async Task Clear_start_inherits_the_unique_recent_closed_session_binding()
    {
        var now = DateTimeOffset.Now;
        var store = new ClaudeSessionStateStore(_paths);
        var oldState = State("old-session", ClaudeSessionStatus.Closed, 26100);
        oldState.LastHookEvent = "SessionEnd";
        oldState.UpdatedAt = now.AddSeconds(-1);
        await store.SaveAtomicAsync(oldState);
        var bindings = new ManualTerminalBindingStore(_paths);
        bindings.Save(new ManualTerminalBinding(
            "old-session",
            "",
            26100,
            "bash",
            @"C:\Users\admin",
            now.AddMinutes(-10),
            "bridge-one"));

        var migration = await new ClearSessionTerminalBindingMigrator(_paths)
            .TryMigrateAsync(
                new HookEvent
                {
                    Kind = HookEventKind.SessionStart,
                    RawEventName = "SessionStart",
                    SessionId = "new-session",
                    WorkingDirectory = @"C:\Users\admin",
                    SessionStartSource = "clear"
                },
                now);

        Assert.NotNull(migration);
        Assert.Equal("old-session", migration.PreviousSessionId);
        Assert.Equal(26100, migration.Binding.TerminalProcessId);
        Assert.Null(bindings.TryLoad("old-session"));
        Assert.Equal(26100, bindings.TryLoad("new-session")!.TerminalProcessId);
        Assert.Equal(
            "new-session",
            (await store.TryLoadAsync("old-session"))!.SupersededBySessionId);
    }

    [Fact]
    public void Clear_handoff_can_capture_the_session_identity_without_a_manual_binding()
    {
        var now = DateTimeOffset.Now;
        var state = State("old-session", ClaudeSessionStatus.Running, 5128);
        state.TerminalToken = "";
        var bindings = new ManualTerminalBindingStore(_paths);

        var preserved = bindings.PreserveForClear(state, now);

        Assert.NotNull(preserved);
        Assert.Equal(5128, preserved.TerminalProcessId);
        Assert.Equal(
            5128,
            bindings.TryLoad("old-session")!.TerminalProcessId);
    }

    [Fact]
    public async Task Normal_start_does_not_inherit_a_closed_session_binding()
    {
        var now = DateTimeOffset.Now;
        var store = new ClaudeSessionStateStore(_paths);
        var oldState = State("old-session", ClaudeSessionStatus.Closed, 26100);
        oldState.LastHookEvent = "SessionEnd";
        oldState.UpdatedAt = now.AddSeconds(-1);
        await store.SaveAtomicAsync(oldState);
        var bindings = new ManualTerminalBindingStore(_paths);
        bindings.Save(new ManualTerminalBinding(
            "old-session",
            "",
            26100,
            "bash",
            @"C:\Users\admin",
            now,
            "bridge-one"));

        var migration = await new ClearSessionTerminalBindingMigrator(_paths)
            .TryMigrateAsync(
                new HookEvent
                {
                    Kind = HookEventKind.SessionStart,
                    RawEventName = "SessionStart",
                    SessionId = "new-session",
                    WorkingDirectory = @"C:\Users\admin",
                    SessionStartSource = "startup"
                },
                now);

        Assert.Null(migration);
        Assert.NotNull(bindings.TryLoad("old-session"));
        Assert.Null(bindings.TryLoad("new-session"));
    }

    [Fact]
    public async Task Clear_start_does_not_guess_between_two_recent_bindings()
    {
        var now = DateTimeOffset.Now;
        var store = new ClaudeSessionStateStore(_paths);
        var bindings = new ManualTerminalBindingStore(_paths);
        foreach (var (sessionId, processId) in new[]
                 {
                     ("old-one", 26100),
                     ("old-two", 26101)
                 })
        {
            var state = State(sessionId, ClaudeSessionStatus.Closed, processId);
            state.LastHookEvent = "SessionEnd";
            state.UpdatedAt = now.AddSeconds(-1);
            await store.SaveAtomicAsync(state);
            bindings.Save(new ManualTerminalBinding(
                sessionId,
                "",
                processId,
                "bash",
                @"C:\Users\admin",
                now,
                ""));
        }

        var migration = await new ClearSessionTerminalBindingMigrator(_paths)
            .TryMigrateAsync(
                new HookEvent
                {
                    Kind = HookEventKind.SessionStart,
                    RawEventName = "SessionStart",
                    SessionId = "new-session",
                    WorkingDirectory = @"C:\Users\admin",
                    SessionStartSource = "clear"
                },
                now);

        Assert.Null(migration);
        Assert.Null(bindings.TryLoad("new-session"));
    }

    [Fact]
    public async Task Clear_start_prefers_the_only_focused_active_binding()
    {
        var now = DateTimeOffset.Now;
        var store = new ClaudeSessionStateStore(_paths);
        var bindings = new ManualTerminalBindingStore(_paths);
        foreach (var (sessionId, processId, bridgeId) in new[]
                 {
                     ("old-one", 26100, "bridge-one"),
                     ("old-two", 26101, "bridge-two")
                 })
        {
            var state = State(sessionId, ClaudeSessionStatus.Closed, processId);
            state.LastHookEvent = "SessionEnd";
            state.UpdatedAt = now.AddSeconds(-1);
            await store.SaveAtomicAsync(state);
            bindings.Save(new ManualTerminalBinding(
                sessionId,
                "",
                processId,
                "bash",
                @"C:\Users\admin",
                now,
                bridgeId));
        }

        WriteBridgeWithWindowState(
            "bridge-one",
            26100,
            false,
            new TerminalBridgeTerminal(
                "terminal-one",
                "",
                "bash one",
                26100,
                ""));
        WriteBridgeWithWindowState(
            "bridge-two",
            26101,
            true,
            new TerminalBridgeTerminal(
                "terminal-two",
                "",
                "bash two",
                26101,
                ""));

        var migration = await new ClearSessionTerminalBindingMigrator(_paths)
            .TryMigrateAsync(
                new HookEvent
                {
                    Kind = HookEventKind.SessionStart,
                    RawEventName = "SessionStart",
                    SessionId = "new-session",
                    WorkingDirectory = @"C:\Users\admin",
                    SessionStartSource = "clear"
                },
                now);

        Assert.NotNull(migration);
        Assert.Equal("old-two", migration.PreviousSessionId);
        Assert.Equal(26101, migration.Binding.TerminalProcessId);
        Assert.NotNull(bindings.TryLoad("old-one"));
        Assert.Null(bindings.TryLoad("old-two"));
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

    private void WriteBridgeWithWindowState(
        string bridgeId,
        int activeTerminalProcessId,
        bool windowFocused,
        params TerminalBridgeTerminal[] terminals)
        => WriteBridgeWithWindowState(
            bridgeId,
            activeTerminalProcessId,
            windowFocused,
            [],
            terminals);

    private void WriteBridgeWithWindowState(
        string bridgeId,
        int activeTerminalProcessId,
        bool windowFocused,
        IReadOnlyList<string> workspaceFolders,
        params TerminalBridgeTerminal[] terminals)
    {
        var registration = new TerminalBridgeRegistration(
            3,
            bridgeId,
            123,
            DateTimeOffset.UtcNow,
            "",
            workspaceFolders,
            terminals,
            activeTerminalProcessId,
            windowFocused);
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
