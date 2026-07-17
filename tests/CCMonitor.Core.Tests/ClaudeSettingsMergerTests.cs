using CCMonitor.Core.Services;
using System.Text.Json.Nodes;
using Xunit;

namespace CCMonitor.Core.Tests;

public sealed class ClaudeSettingsMergerTests
{
    private const string Command = @"C:\Tools\CCMonitor.Hook.exe";
    private readonly ClaudeSettingsMerger _merger = new();

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"model":"opus"}""")]
    [InlineData("""{"hooks":{}}""")]
    [InlineData("""{"hooks":{"PostToolUse":[]}}""")]
    [InlineData("""{"hooks":{"Notification":[{"matcher":"custom","hooks":[]}]}}""")]
    public void Install_preserves_existing_json_and_adds_hooks(string input)
    {
        var installed = _merger.Install(input, Command);
        Assert.True(_merger.IsInstalled(installed, Command));
        Assert.Contains(Command.Replace(@"\", @"\\"), installed);
    }

    [Fact]
    public void Install_is_idempotent()
    {
        var once = _merger.Install("{}", Command);
        var twice = _merger.Install(once, Command);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Install_uses_one_catch_all_notification_hook()
    {
        var installed = _merger.Install("{}", Command);
        var root = JsonNode.Parse(installed)!.AsObject();
        var notifications = root["hooks"]!["Notification"]!.AsArray();

        Assert.Single(notifications);
        Assert.Equal("", notifications[0]!["matcher"]!.GetValue<string>());
    }

    [Fact]
    public void Install_configures_status_line_refresh()
    {
        const string statusLineCommand = "CCMonitor.StatusLine.exe";
        var installed = _merger.Install("{}", Command, statusLineCommand);
        var root = JsonNode.Parse(installed)!.AsObject();

        Assert.Equal(
            ClaudeSettingsMerger.StatusLineRefreshIntervalSeconds,
            root["statusLine"]!["refreshInterval"]!.GetValue<int>());
        Assert.True(_merger.IsInstalled(installed, Command, statusLineCommand));
    }

    [Fact]
    public void Install_rejects_invalid_json()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => _merger.Install("{ invalid", Command));
        Assert.False(_merger.IsInstalled("{ invalid", Command));
    }

    [Fact]
    public void Install_replaces_unquoted_windows_hook_command()
    {
        var input = """
        {
          "hooks": {
            "SessionStart": [
              {
                "matcher": "",
                "hooks": [
                  {
                    "type": "command",
                    "command": "C:\\Users\\ExampleUser\\Desktop\\CC Monitor\\CCMonitor.Hook.exe"
                  }
                ]
              }
            ]
          }
        }
        """;

        var shellSafe = "'/c/Users/ExampleUser/Desktop/CC Monitor/CCMonitor.Hook.exe'";
        var installed = _merger.Install(input, shellSafe);
        var root = JsonNode.Parse(installed)!.AsObject();
        var command = root["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();

        Assert.True(_merger.IsInstalled(installed, shellSafe));
        Assert.Equal(shellSafe, command);
        Assert.DoesNotContain("C:\\\\Users\\\\ExampleUser", installed);
    }

    [Fact]
    public void Uninstall_removes_only_cc_monitor_hooks()
    {
        var input = """
        {
          "model": "opus",
          "hooks": {
            "PostToolUse": [
              {
                "matcher": "",
                "hooks": [
                  {
                    "type": "command",
                    "command": "custom.exe"
                  }
                ]
              }
            ]
          }
        }
        """;

        var installed = _merger.Install(input, Command);
        var uninstalled = _merger.Uninstall(installed, Command);

        Assert.False(_merger.IsInstalled(uninstalled, Command));
        Assert.Contains("custom.exe", uninstalled);
        Assert.Contains("opus", uninstalled);
    }

    [Fact]
    public void File_service_restores_existing_status_line_on_uninstall()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ccmonitor-tests-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            const string original = """
            {
              "model": "opus",
              "statusLine": {
                "type": "command",
                "command": "my-status-line.exe",
                "refreshInterval": 12
              }
            }
            """;
            File.WriteAllText(settingsPath, original);
            var service = new ClaudeSettingsFileService(settingsPath);

            service.Install(Command, "CCMonitor.StatusLine.exe");
            Assert.True(service.IsInstalled(Command, "CCMonitor.StatusLine.exe"));
            Assert.True(File.Exists($"{settingsPath}.ccmonitor.bak"));

            service.Uninstall(Command, "CCMonitor.StatusLine.exe");
            var restored = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

            Assert.Equal("opus", restored["model"]!.GetValue<string>());
            Assert.Equal("my-status-line.exe", restored["statusLine"]!["command"]!.GetValue<string>());
            Assert.Equal(12, restored["statusLine"]!["refreshInterval"]!.GetValue<int>());
            Assert.False(File.Exists($"{settingsPath}.ccmonitor-statusline-backup.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void File_service_does_not_overwrite_invalid_settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ccmonitor-tests-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            const string invalid = "{ invalid";
            File.WriteAllText(settingsPath, invalid);
            var service = new ClaudeSettingsFileService(settingsPath);

            Assert.ThrowsAny<System.Text.Json.JsonException>(() => service.Install(Command));
            Assert.Equal(invalid, File.ReadAllText(settingsPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void File_service_preserves_status_line_changed_after_install()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ccmonitor-tests-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(settingsPath, """{"statusLine":{"type":"command","command":"old.exe"}}""");
            var service = new ClaudeSettingsFileService(settingsPath);
            service.Install(Command, "CCMonitor.StatusLine.exe");

            var changed = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
            changed["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = "new.exe"
            };
            File.WriteAllText(settingsPath, changed.ToJsonString());

            service.Uninstall(Command, "CCMonitor.StatusLine.exe");
            var uninstalled = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

            Assert.Equal("new.exe", uninstalled["statusLine"]!["command"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
