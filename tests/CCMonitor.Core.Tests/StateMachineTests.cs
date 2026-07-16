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

    [Theory]
    [InlineData(ClaudeSessionStatus.Idle)]
    [InlineData(ClaudeSessionStatus.Done)]
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
    public void StopFailure_sets_error()
    {
        var state = NewState(ClaudeSessionStatus.Running);
        state.BlockedAt = DateTimeOffset.Now;
        state.BlockedReason = "Permission required: Bash";
        _machine.Apply(state, Event(HookEventKind.StopFailure), _config);
        Assert.Equal(ClaudeSessionStatus.Error, state.Status);
        Assert.NotNull(state.FailedAt);
        Assert.Null(state.BlockedAt);
        Assert.Null(state.BlockedReason);
    }

    [Fact]
    public void SessionEnd_sets_closed()
    {
        var state = NewState(ClaudeSessionStatus.Blocked);
        _machine.Apply(state, Event(HookEventKind.SessionEnd), _config);
        Assert.Equal(ClaudeSessionStatus.Closed, state.Status);
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
