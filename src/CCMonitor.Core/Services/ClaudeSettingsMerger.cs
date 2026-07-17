using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCMonitor.Core.Services;

public sealed class ClaudeSettingsMerger
{
    public const int StatusLineRefreshIntervalSeconds = 5;

    public static readonly string[] EventNames =
    [
        "SessionStart",
        "UserPromptSubmit",
        "PermissionRequest",
        "PreToolUse",
        "PostToolUse",
        "Stop",
        "StopFailure",
        "SessionEnd"
    ];

    public string Install(string settingsJson, string hookCommand, string? statusLineCommand = null)
    {
        var root = ParseObjectOrNew(settingsJson);
        var hooks = GetOrCreateObject(root, "hooks");

        RemoveExistingCcMonitorCommands(hooks);

        foreach (var eventName in EventNames)
        {
            AddEntry(hooks, eventName, "", hookCommand);
        }

        AddEntry(hooks, "Notification", "", hookCommand);
        if (!string.IsNullOrWhiteSpace(statusLineCommand))
        {
            root["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = statusLineCommand,
                ["refreshInterval"] = StatusLineRefreshIntervalSeconds
            };
        }

        return root.ToJsonString(Options);
    }

    public string Uninstall(string settingsJson, string hookCommand, string? statusLineCommand = null)
    {
        var root = ParseObjectOrNew(settingsJson);
        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var property in hooks.ToList())
            {
                if (property.Value is not JsonArray entries) continue;
                RemoveCommand(entries, hookCommand);
                RemoveCcMonitorHookCommands(entries);
                if (entries.Count == 0) hooks.Remove(property.Key);
            }
        }

        if (root["statusLine"] is JsonObject statusLine
            && statusLine["type"]?.GetValue<string>() == "command"
            && IsCcMonitorStatusLineCommand(statusLine["command"]?.GetValue<string>(), statusLineCommand))
        {
            root.Remove("statusLine");
        }

        return root.ToJsonString(Options);
    }

    public bool IsInstalled(string settingsJson, string hookCommand, string? statusLineCommand = null)
    {
        JsonObject root;
        try
        {
            root = ParseObjectOrNew(settingsJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root["hooks"] is not JsonObject hooks) return false;

        var hooksInstalled = EventNames.All(e => ContainsCommand(hooks[e] as JsonArray, hookCommand))
            && ContainsCommand(hooks["Notification"] as JsonArray, hookCommand, "");

        if (!hooksInstalled) return false;
        if (string.IsNullOrWhiteSpace(statusLineCommand)) return true;

        return root["statusLine"] is JsonObject statusLine
            && statusLine["type"]?.GetValue<string>() == "command"
            && string.Equals(statusLine["command"]?.GetValue<string>(), statusLineCommand, StringComparison.OrdinalIgnoreCase)
            && statusLine["refreshInterval"]?.GetValue<int>() == StatusLineRefreshIntervalSeconds;
    }

    private static void AddEntry(JsonObject hooks, string eventName, string matcher, string hookCommand)
    {
        JsonArray entries;
        if (hooks[eventName] is JsonArray existingEntries)
        {
            entries = existingEntries;
        }
        else
        {
            entries = new JsonArray();
            hooks[eventName] = entries;
        }

        if (ContainsCommand(entries, hookCommand, matcher)) return;

        entries.Add(new JsonObject
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = hookCommand
                }
            }
        });
    }

    private static void RemoveCommand(JsonArray entries, string hookCommand)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is not JsonObject entry) continue;
            if (entry["hooks"] is not JsonArray commands) continue;

            for (var j = commands.Count - 1; j >= 0; j--)
            {
                if (commands[j] is JsonObject command
                    && command["type"]?.GetValue<string>() == "command"
                    && string.Equals(command["command"]?.GetValue<string>(), hookCommand, StringComparison.OrdinalIgnoreCase))
                {
                    commands.RemoveAt(j);
                }
            }

            if (commands.Count == 0) entries.RemoveAt(i);
        }
    }

    private static void RemoveExistingCcMonitorCommands(JsonObject hooks)
    {
        foreach (var property in hooks.ToList())
        {
            if (property.Value is not JsonArray entries) continue;
            RemoveCcMonitorHookCommands(entries);
            if (entries.Count == 0) hooks.Remove(property.Key);
        }
    }

    private static void RemoveCcMonitorHookCommands(JsonArray entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is not JsonObject entry) continue;
            if (entry["hooks"] is not JsonArray commands) continue;

            for (var j = commands.Count - 1; j >= 0; j--)
            {
                if (commands[j] is JsonObject command
                    && command["type"]?.GetValue<string>() == "command"
                    && IsCcMonitorHookCommand(command["command"]?.GetValue<string>()))
                {
                    commands.RemoveAt(j);
                }
            }

            if (commands.Count == 0) entries.RemoveAt(i);
        }
    }

    private static bool IsCcMonitorHookCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        return command.Trim('"').Replace('\\', '/').EndsWith("/CCMonitor.Hook.exe", StringComparison.OrdinalIgnoreCase)
            || command.Contains("CCMonitor.Hook.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCcMonitorStatusLineCommand(string? command, string? expectedCommand)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (!string.IsNullOrWhiteSpace(expectedCommand)
            && string.Equals(command, expectedCommand, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return command.Trim('"').Replace('\\', '/').EndsWith("/CCMonitor.StatusLine.exe", StringComparison.OrdinalIgnoreCase)
            || command.Contains("CCMonitor.StatusLine.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCommand(JsonArray? entries, string hookCommand, string? matcher = null)
    {
        if (entries is null) return false;

        foreach (var node in entries)
        {
            if (node is not JsonObject entry) continue;
            if (matcher is not null && !string.Equals(entry["matcher"]?.GetValue<string>() ?? "", matcher, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry["hooks"] is not JsonArray commands) continue;
            foreach (var commandNode in commands)
            {
                if (commandNode is JsonObject command
                    && command["type"]?.GetValue<string>() == "command"
                    && string.Equals(command["command"]?.GetValue<string>(), hookCommand, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonObject ParseObjectOrNew(string settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            throw new JsonException("Claude settings are empty or invalid.");
        }

        try
        {
            var node = JsonNode.Parse(settingsJson)
                ?? throw new JsonException("Claude settings did not contain a JSON value.");
            return node as JsonObject
                ?? throw new JsonException("Claude settings must contain a JSON object at the root.");
        }
        catch (JsonException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException("Claude settings must contain a JSON object at the root.", exception);
        }
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject existing) return existing;
        var created = new JsonObject();
        root[propertyName] = created;
        return created;
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
