using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace CCMonitor.Core.HookEvents;

public sealed class HookEventParser
{
    public HookEvent Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new HookEvent { Kind = HookEventKind.Unknown, RawEventName = "EmptyInput" };
        }

        var normalizedInput = input.Trim().TrimStart('\uFEFF', '\0');
        if (TryParseDocument(normalizedInput, out var document, out var parseError))
        {
            return ParseDocument(document!, wasRecovered: false, parseError: null);
        }

        var recoveryError = "No JSON object start was found.";
        var firstObject = normalizedInput.IndexOf('{');
        if (firstObject >= 0
            && TryParseFirstJsonValue(normalizedInput[firstObject..], out document, out recoveryError))
        {
            return ParseDocument(
                document!,
                wasRecovered: true,
                parseError: $"Exact JSON parse failed: {parseError}. Recovered first JSON value.");
        }

        if (TryRecoverEssentialFields(normalizedInput, out var recovered))
        {
            return recovered with
            {
                WasRecovered = true,
                ParseError = $"JSON parse failed: {parseError}. Recovery parse failed: {recoveryError}."
            };
        }

        return new HookEvent
        {
            Kind = HookEventKind.Unknown,
            RawEventName = "InvalidJson",
            ParseError = parseError
        };
    }

    private static HookEvent ParseDocument(JsonDocument document, bool wasRecovered, string? parseError)
    {
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
        var sessionStartSource = GetString(root, "source");
        var sessionEndReason = GetString(root, "reason");

        return new HookEvent
        {
            Kind = DetermineKind(eventName, notificationType),
            RawEventName = string.IsNullOrWhiteSpace(eventName) ? "Unknown" : eventName,
            SessionId = sessionId,
            WorkingDirectory = cwd,
            Prompt = prompt,
            ToolName = toolName,
            NotificationType = notificationType,
            SessionStartSource = sessionStartSource,
            SessionEndReason = sessionEndReason,
            RawJson = document,
            WasRecovered = wasRecovered,
            ParseError = parseError
        };
    }

    private static bool TryParseDocument(string input, out JsonDocument? document, out string error)
    {
        try
        {
            document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            error = "";
            return true;
        }
        catch (JsonException exception)
        {
            document = null;
            error = CompactError(exception.Message);
            return false;
        }
    }

    private static bool TryParseFirstJsonValue(string input, out JsonDocument? document, out string error)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            document = JsonDocument.ParseValue(ref reader);
            error = "";
            return true;
        }
        catch (JsonException exception)
        {
            document = null;
            error = CompactError(exception.Message);
            return false;
        }
    }

    private static bool TryRecoverEssentialFields(string input, out HookEvent recovered)
    {
        var eventName = ExtractJsonString(input, "hook_event_name")
            ?? ExtractJsonString(input, "event_name")
            ?? ExtractJsonString(input, "event")
            ?? ExtractJsonString(input, "type");
        var sessionId = ExtractJsonString(input, "session_id")
            ?? ExtractJsonString(input, "sessionId")
            ?? ExtractJsonString(input, "conversation_id");

        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(sessionId))
        {
            recovered = new HookEvent();
            return false;
        }

        var notificationType = ExtractJsonString(input, "notification_type")
            ?? ExtractJsonString(input, "notificationType")
            ?? ExtractJsonString(input, "matcher")
            ?? ExtractJsonString(input, "message");

        recovered = new HookEvent
        {
            Kind = DetermineKind(eventName, notificationType),
            RawEventName = eventName,
            SessionId = sessionId,
            WorkingDirectory = ExtractJsonString(input, "cwd")
                ?? ExtractJsonString(input, "working_directory")
                ?? ExtractJsonString(input, "workingDirectory"),
            Prompt = ExtractJsonString(input, "prompt")
                ?? ExtractJsonString(input, "user_prompt")
                ?? ExtractJsonString(input, "message"),
            ToolName = ExtractJsonString(input, "tool_name")
                ?? ExtractJsonString(input, "toolName")
                ?? ExtractJsonString(input, "tool"),
            NotificationType = notificationType,
            SessionStartSource = ExtractJsonString(input, "source"),
            SessionEndReason = ExtractJsonString(input, "reason")
        };
        return recovered.Kind != HookEventKind.Unknown;
    }

    private static string? ExtractJsonString(string input, string propertyName)
    {
        var match = Regex.Match(
            input,
            $"[\"']{Regex.Escape(propertyName)}[\"']\\s*:\\s*[\"'](?<value>(?:\\\\.|[^\"'])*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var value = match.Groups["value"].Value;
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{value.Replace("\"", "\\\"")}\"");
        }
        catch
        {
            return value;
        }
    }

    private static string CompactError(string message)
    {
        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? message;
        return firstLine.Length <= 240 ? firstLine : firstLine[..240];
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
