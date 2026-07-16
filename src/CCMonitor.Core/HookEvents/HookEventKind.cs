namespace CCMonitor.Core.HookEvents;

public enum HookEventKind
{
    Unknown,
    SessionStart,
    UserPromptSubmit,
    PermissionRequest,
    NotificationPermissionPrompt,
    NotificationIdlePrompt,
    Activity,
    Stop,
    StopFailure,
    SessionEnd
}
