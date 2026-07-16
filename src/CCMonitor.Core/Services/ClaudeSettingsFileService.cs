using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCMonitor.Core.Services;

public sealed class ClaudeSettingsFileService
{
    private readonly ClaudeSettingsMerger _merger = new();
    private readonly string _backupPath;
    private readonly string _statusLineBackupPath;

    public string SettingsPath { get; }

    public ClaudeSettingsFileService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "settings.json");
        _backupPath = $"{SettingsPath}.ccmonitor.bak";
        _statusLineBackupPath = $"{SettingsPath}.ccmonitor-statusline-backup.json";
    }

    public bool IsInstalled(string hookCommand, string? statusLineCommand = null)
    {
        var json = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "{}";
        return _merger.IsInstalled(json, hookCommand, statusLineCommand);
    }

    public void Install(string hookCommand, string? statusLineCommand = null)
    {
        var json = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "{}";
        var merged = _merger.Install(json, hookCommand, statusLineCommand);

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        BackupSettingsFile();
        BackupExistingStatusLine(json, statusLineCommand);
        WriteAtomic(SettingsPath, merged);
    }

    public void Uninstall(string hookCommand, string? statusLineCommand = null)
    {
        var json = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "{}";
        var merged = _merger.Uninstall(json, hookCommand, statusLineCommand);
        merged = RestorePreviousStatusLine(merged);

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        BackupSettingsFile();
        WriteAtomic(SettingsPath, merged);
        if (File.Exists(_statusLineBackupPath)) File.Delete(_statusLineBackupPath);
    }

    private void BackupSettingsFile()
    {
        if (File.Exists(SettingsPath)) File.Copy(SettingsPath, _backupPath, overwrite: true);
    }

    private void BackupExistingStatusLine(string settingsJson, string? statusLineCommand)
    {
        if (string.IsNullOrWhiteSpace(statusLineCommand) || File.Exists(_statusLineBackupPath)) return;

        var root = JsonNode.Parse(settingsJson)?.AsObject();
        var statusLine = root?["statusLine"];
        if (statusLine is null || IsCcMonitorStatusLine(statusLine)) return;

        WriteAtomic(_statusLineBackupPath, statusLine.ToJsonString(JsonOptions));
    }

    private string RestorePreviousStatusLine(string settingsJson)
    {
        if (!File.Exists(_statusLineBackupPath)) return settingsJson;

        var root = JsonNode.Parse(settingsJson)?.AsObject()
            ?? throw new JsonException("Claude settings must contain a JSON object at the root.");
        if (root["statusLine"] is not null) return settingsJson;

        var previousStatusLine = JsonNode.Parse(File.ReadAllText(_statusLineBackupPath))
            ?? throw new JsonException("The saved status line backup is invalid.");
        root["statusLine"] = previousStatusLine;
        return root.ToJsonString(JsonOptions);
    }

    private static bool IsCcMonitorStatusLine(JsonNode statusLine)
    {
        return statusLine is JsonObject statusLineObject
            && statusLineObject["type"]?.GetValue<string>() == "command"
            && statusLineObject["command"]?.GetValue<string>()?.Contains(
                "CCMonitor.StatusLine.exe",
                StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void WriteAtomic(string path, string json)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
