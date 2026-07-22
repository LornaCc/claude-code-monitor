using CCMonitor.Core.HookEvents;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;
using Xunit;

namespace CCMonitor.Core.Tests;

public sealed class StateMachineTests
{
    private readonly ClaudeSessionStateMachine _machine = new();
    private readonly MonitorConfig _config = new();

    [Fact]
    public void SessionStart_sets_idle()
    {
        var state = NewState(ClaudeSessionStatus.Running);
        _machine.Apply(state, Event(HookEventKind.SessionStart), _config);
        Assert.Equal(ClaudeSessionStatus.Idle, state.Status);
    }

    [Fact]
    public void SessionStart_replaces_stale_terminal_identity()
    {
        var state = NewState(ClaudeSessionStatus.Closed);
        state.TerminalToken = "old-terminal-token";
        state.TerminalProcessId = 11111;
        state.SupersededBySessionId = "replacement-session";

        _machine.Apply(
            state,
            Event(HookEventKind.SessionStart) with
            {
                TerminalProcessId = 22222
            },
            _config);

        Assert.Equal("", state.TerminalToken);
        Assert.Equal(22222, state.TerminalProcessId);
        Assert.Null(state.SupersededBySessionId);
    }

    [Fact]
    public void Resumed_prompt_clears_superseded_marker()
    {
        var state = NewState(ClaudeSessionStatus.Closed);
        state.SupersededBySessionId = "replacement-session";

        _machine.Apply(state, Event(HookEventKind.UserPromptSubmit), _config);

        Assert.Equal(ClaudeSessionStatus.Running, state.Status);
        Assert.Null(state.SupersededBySessionId);
    }

    [Theory]
    [InlineData(ClaudeSessionStatus.Idle)]
    [InlineData(ClaudeSessionStatus.Done)]
    [InlineData(ClaudeSessionStatus.Interrupted)]
    [InlineData(ClaudeSessionStatus.Error)]
    [InlineData(ClaudeSessionStatus.Blocked)]
    public void UserPromptSubmit_sets_running(ClaudeSessionStatus from)
    {
        var state = NewState(from);
        _machine.Apply(state, Event(HookEventKind.UserPromptSubmit), _config);
        Assert.Equal(ClaudeSessionStatus.Running, state.Status);
        Assert.NotNull(state.StartedAt);
    }

    [Fact]
    public void PermissionRequest_sets_blocked()
    {
        var state = NewState(ClaudeSessionStatus.Running);
        _machine.Apply(state, Event(HookEventKind.PermissionRequest, toolName: "Bash"), _config);
        Assert.Equal(ClaudeSessionStatus.Blocked, state.Status);
        Assert.Equal("Permission required: Bash", state.BlockedReason);
    }

    [Fact]
    public void Activity_after_blocked_sets_running()
    {
        var state = NewState(ClaudeSessionStatus.Blocked);
        state.BlockedAt = DateTimeOffset.Now;
        state.BlockedReason = "Permission required: Bash";

        _machine.Apply(state, Event(HookEventKind.Activity), _config);

        Assert.Equal(ClaudeSessionStatus.Running, state.Status);
        Assert.Null(state.BlockedAt);
        Assert.Null(state.BlockedReason);
    }

    [Theory]
    [InlineData(ClaudeSessionStatus.Running)]
    [InlineData(ClaudeSessionStatus.Blocked)]
    public void Stop_sets_done(ClaudeSessionStatus from)
    {
        var state = NewState(from);
        state.BlockedAt = DateTimeOffset.Now;
        state.BlockedReason = "Permission required: Bash";
        _machine.Apply(state, Event(HookEventKind.Stop), _config);
        Assert.Equal(ClaudeSessionStatus.Done, state.Status);
        Assert.NotNull(state.FinishedAt);
        Assert.Null(state.BlockedAt);
        Assert.Null(state.BlockedReason);
    }

    [Fact]
    public void StopFailure_sets_interrupted()
    {
        var state = NewState(ClaudeSessionStatus.Running);
        state.BlockedAt = DateTimeOffset.Now;
        state.BlockedReason = "Permission required: Bash";
        _machine.Apply(state, Event(HookEventKind.StopFailure), _config);
        Assert.Equal(ClaudeSessionStatus.Interrupted, state.Status);
        Assert.NotNull(state.FinishedAt);
        Assert.Null(state.FailedAt);
        Assert.Null(state.BlockedAt);
        Assert.Null(state.BlockedReason);
    }

    [Fact]
    public void Stop_preserves_transcript_interrupted_state()
    {
        var interruptedAt = DateTimeOffset.Now.AddSeconds(-1);
        var state = NewState(ClaudeSessionStatus.Interrupted);
        state.InterruptedAt = interruptedAt;
        state.FinishedAt = interruptedAt;

        _machine.Apply(
            state,
            Event(HookEventKind.Stop),
            _config,
            interruptedAt.AddSeconds(10));

        Assert.Equal(ClaudeSessionStatus.Interrupted, state.Status);
        Assert.Equal(interruptedAt, state.FinishedAt);
    }

    [Fact]
    public void New_prompt_clears_previous_interrupt_marker()
    {
        var state = NewState(ClaudeSessionStatus.Interrupted);
        state.InterruptedAt = DateTimeOffset.Now.AddSeconds(-1);

        _machine.Apply(state, Event(HookEventKind.UserPromptSubmit), _config);

        Assert.Equal(ClaudeSessionStatus.Running, state.Status);
        Assert.Null(state.InterruptedAt);
    }

    [Fact]
    public void Delayed_permission_event_does_not_overwrite_interrupted_state()
    {
        var interruptedAt = DateTimeOffset.Now.AddSeconds(-1);
        var state = NewState(ClaudeSessionStatus.Interrupted);
        state.InterruptedAt = interruptedAt;

        _machine.Apply(
            state,
            Event(HookEventKind.PermissionRequest, toolName: "WebSearch"),
            _config);

        Assert.Equal(ClaudeSessionStatus.Interrupted, state.Status);
        Assert.Equal(interruptedAt, state.InterruptedAt);
        Assert.Null(state.BlockedAt);
    }

    [Fact]
    public void Hook_terminal_process_id_is_persisted()
    {
        var state = NewState(ClaudeSessionStatus.Idle);
        var hookEvent = Event(HookEventKind.UserPromptSubmit) with
        {
            TerminalProcessId = 24680
        };

        _machine.Apply(state, hookEvent, _config);

        Assert.Equal(24680, state.TerminalProcessId);
    }

    [Fact]
    public void SessionEnd_sets_closed()
    {
        var state = NewState(ClaudeSessionStatus.Blocked);
        state.TerminalToken = "terminal-token";
        state.TerminalProcessId = 24680;
        _machine.Apply(state, Event(HookEventKind.SessionEnd), _config);
        Assert.Equal(ClaudeSessionStatus.Closed, state.Status);
        Assert.Equal("", state.TerminalToken);
        Assert.Null(state.TerminalProcessId);
    }

    private static ClaudeSessionState NewState(ClaudeSessionStatus status) => new()
    {
        SessionId = "abc123",
        Status = status,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now
    };

    private static HookEvent Event(HookEventKind kind, string? toolName = null) => new()
    {
        Kind = kind,
        RawEventName = kind.ToString(),
        SessionId = "abc123",
        WorkingDirectory = @"C:\Users\ExampleUser\Desktop\project",
        ToolName = toolName
    };
}
