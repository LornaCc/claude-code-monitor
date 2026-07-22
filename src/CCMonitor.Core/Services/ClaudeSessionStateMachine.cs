using CCMonitor.Core.HookEvents;
using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class ClaudeSessionStateMachine
{
    public void Apply(ClaudeSessionState state, HookEvent hookEvent, MonitorConfig config, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.Now;
        state.UpdatedAt = timestamp;
        state.LastHookEvent = hookEvent.RawEventName;

        if (hookEvent.Kind == HookEventKind.SessionStart)
        {
            // A resumed conversation can start in a different terminal than the one
            // that last owned this session id. Never carry that terminal identity
            // across a new Claude runtime attachment.
            state.TerminalToken = "";
            state.TerminalProcessId = null;
        }

        if (!string.IsNullOrWhiteSpace(hookEvent.TerminalToken))
        {
            state.TerminalToken = hookEvent.TerminalToken!;
        }

        if (hookEvent.TerminalProcessId is > 0)
        {
            state.TerminalProcessId = hookEvent.TerminalProcessId;
        }

        if (!string.IsNullOrWhiteSpace(hookEvent.WorkingDirectory))
        {
            state.WorkingDirectory = hookEvent.WorkingDirectory!;
            state.ProjectName = ProjectNameResolver.FromWorkingDirectory(hookEvent.WorkingDirectory);
        }

        switch (hookEvent.Kind)
        {
            case HookEventKind.SessionStart:
                state.Status = ClaudeSessionStatus.Idle;
                state.InterruptedAt = null;
                state.SupersededBySessionId = null;
                break;
            case HookEventKind.UserPromptSubmit:
                state.Status = ClaudeSessionStatus.Running;
                state.StartedAt = timestamp;
                state.BlockedAt = null;
                state.FinishedAt = null;
                state.FailedAt = null;
                state.InterruptedAt = null;
                state.BlockedReason = null;
                state.PromptPreview = config.SavePromptPreview ? Truncate(hookEvent.Prompt, 100) : null;
                state.SupersededBySessionId = null;
                break;
            case HookEventKind.PermissionRequest:
            case HookEventKind.NotificationPermissionPrompt:
                if (state.Status != ClaudeSessionStatus.Interrupted)
                {
                    state.Status = ClaudeSessionStatus.Blocked;
                    state.BlockedAt = timestamp;
                    state.BlockedReason = string.IsNullOrWhiteSpace(hookEvent.ToolName)
                        ? "Permission required"
                        : $"Permission required: {hookEvent.ToolName}";
                }
                break;
            case HookEventKind.Activity:
                if (state.Status == ClaudeSessionStatus.Blocked)
                {
                    state.Status = ClaudeSessionStatus.Running;
                    state.BlockedAt = null;
                    state.BlockedReason = null;
                    state.StartedAt ??= timestamp;
                }
                break;
            case HookEventKind.NotificationIdlePrompt:
                if (state.Status != ClaudeSessionStatus.Interrupted)
                {
                    state.Status = ClaudeSessionStatus.Done;
                    state.FinishedAt ??= timestamp;
                }
                state.BlockedAt = null;
                state.BlockedReason = null;
                break;
            case HookEventKind.Stop:
                if (state.Status == ClaudeSessionStatus.Interrupted
                    && state.InterruptedAt is not null)
                {
                    state.FinishedAt = state.InterruptedAt;
                }
                else
                {
                    state.Status = ClaudeSessionStatus.Done;
                    state.FinishedAt = timestamp;
                }
                state.BlockedAt = null;
                state.BlockedReason = null;
                break;
            case HookEventKind.StopFailure:
                state.Status = ClaudeSessionStatus.Interrupted;
                state.FinishedAt = timestamp;
                state.FailedAt = null;
                state.InterruptedAt = timestamp;
                state.BlockedAt = null;
                state.BlockedReason = null;
                break;
            case HookEventKind.SessionEnd:
                state.Status = ClaudeSessionStatus.Closed;
                state.TerminalToken = "";
                state.TerminalProcessId = null;
                break;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
