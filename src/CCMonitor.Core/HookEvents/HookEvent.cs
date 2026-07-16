using System.Text.Json;

namespace CCMonitor.Core.HookEvents;

public sealed class HookEvent
{
    public HookEventKind Kind { get; init; }
    public string RawEventName { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string? WorkingDirectory { get; init; }
    public string? Prompt { get; init; }
    public string? ToolName { get; init; }
    public string? NotificationType { get; init; }
    public JsonDocument? RawJson { get; init; }
}
