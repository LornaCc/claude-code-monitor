using System.Text.Json;

namespace CCMonitor.Core.HookEvents;

public sealed class HookEventParser
{
    public HookEvent Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new HookEvent { Kind = HookEventKind.Unknown, RawEventName = "EmptyInput" };
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(input);
        }
        catch
        {
            return new HookEvent { Kind = HookEventKind.Unknown, RawEventName = "InvalidJson" };
        }

        var root = document.RootElement;
        var eventName = GetString(root, "hook_event_name")
            ?? GetString(root, "event_name")
            ?? GetString(root, "event")
            ?? GetString(root, "type")
            ?? "";
        var sessionId = GetString(root, "session_id")
            ?? GetString(root, "sessionId")
            ?? GetString(root, "conversation_id")
            ?? "";
        var cwd = GetString(root, "cwd")
            ?? GetString(root, "working_directory")
            ?? GetString(root, "workingDirectory");
        var prompt = GetString(root, "prompt")
            ?? GetString(root, "user_prompt")
            ?? GetString(root, "message");
        var toolName = GetString(root, "tool_name")
            ?? GetString(root, "toolName")
            ?? GetString(root, "tool");
        var notificationType = GetString(root, "notification_type")
            ?? GetString(root, "notificationType")
            ?? GetString(root, "matcher")
            ?? GetString(root, "message");

        return new HookEvent
        {
            Kind = DetermineKind(eventName, notificationType),
            RawEventName = string.IsNullOrWhiteSpace(eventName) ? "Unknown" : eventName,
            SessionId = sessionId,
            WorkingDirectory = cwd,
            Prompt = prompt,
            ToolName = toolName,
            NotificationType = notificationType,
            RawJson = document
        };
    }

    private static HookEventKind DetermineKind(string eventName, string? notificationType)
    {
        if (eventName.Equals("SessionStart", StringComparison.OrdinalIgnoreCase)) return HookEventKind.SessionStart;
        if (eventName.Equals("UserPromptSubmit", StringComparison.OrdinalIgnoreCase)) return HookEventKind.UserPromptSubmit;
        if (eventName.Equals("PermissionRequest", StringComparison.OrdinalIgnoreCase)) return HookEventKind.PermissionRequest;
        if (eventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase)) return HookEventKind.Activity;
        if (eventName.Equals("PostToolUse", StringComparison.OrdinalIgnoreCase)) return HookEventKind.Activity;
        if (eventName.Equals("Stop", StringComparison.OrdinalIgnoreCase)) return HookEventKind.Stop;
        if (eventName.Equals("StopFailure", StringComparison.OrdinalIgnoreCase)) return HookEventKind.StopFailure;
        if (eventName.Equals("SessionEnd", StringComparison.OrdinalIgnoreCase)) return HookEventKind.SessionEnd;

        if (eventName.Equals("Notification", StringComparison.OrdinalIgnoreCase))
        {
            var value = notificationType ?? "";
            if (value.Contains("permission_prompt", StringComparison.OrdinalIgnoreCase)) return HookEventKind.NotificationPermissionPrompt;
            if (value.Contains("idle_prompt", StringComparison.OrdinalIgnoreCase)) return HookEventKind.NotificationIdlePrompt;
            if (value.Contains("interrupt", StringComparison.OrdinalIgnoreCase)
                || value.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                || value.Contains("abort", StringComparison.OrdinalIgnoreCase)
                || value.Contains("stopped", StringComparison.OrdinalIgnoreCase))
            {
                return HookEventKind.NotificationIdlePrompt;
            }
        }

        return HookEventKind.Unknown;
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}
