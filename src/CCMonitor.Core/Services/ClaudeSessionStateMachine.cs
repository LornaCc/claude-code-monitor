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

        if (!string.IsNullOrWhiteSpace(hookEvent.TerminalToken))
        {
            state.TerminalToken = hookEvent.TerminalToken!;
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
                break;
            case HookEventKind.UserPromptSubmit:
                state.Status = ClaudeSessionStatus.Running;
                state.StartedAt = timestamp;
                state.BlockedAt = null;
                state.FinishedAt = null;
                state.FailedAt = null;
                state.BlockedReason = null;
                state.PromptPreview = config.SavePromptPreview ? Truncate(hookEvent.Prompt, 100) : null;
                break;
            case HookEventKind.PermissionRequest:
            case HookEventKind.NotificationPermissionPrompt:
                state.Status = ClaudeSessionStatus.Blocked;
                state.BlockedAt = timestamp;
                state.BlockedReason = string.IsNullOrWhiteSpace(hookEvent.ToolName)
                    ? "Permission required"
                    : $"Permission required: {hookEvent.ToolName}";
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
                state.Status = ClaudeSessionStatus.Done;
                state.FinishedAt ??= timestamp;
                state.BlockedAt = null;
                state.BlockedReason = null;
                break;
            case HookEventKind.Stop:
                state.Status = ClaudeSessionStatus.Done;
                state.FinishedAt = timestamp;
                state.BlockedAt = null;
                state.BlockedReason = null;
                break;
            case HookEventKind.StopFailure:
                state.Status = ClaudeSessionStatus.Error;
                state.FailedAt = timestamp;
                state.BlockedAt = null;
                state.BlockedReason = null;
                break;
            case HookEventKind.SessionEnd:
                state.Status = ClaudeSessionStatus.Closed;
                break;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
