using CCMonitor.Core.Models;
using CCMonitor.Core.Services;
using Xunit;

namespace CCMonitor.Core.Tests;

public sealed class TerminalBridgeRegistryTests
{
    private readonly TerminalBridgeRegistry _registry = new(new CcMonitorPaths(
        Path.Combine(Path.GetTempPath(), $"cc-monitor-bridge-tests-{Guid.NewGuid():N}")));

    [Fact]
    public void Select_prefers_exact_terminal_working_directory()
    {
        var first = Bridge(
            "first",
            "project-a",
            [@"C:\work\project-a"],
            [Terminal("one", @"C:\work\project-a")]);
        var second = Bridge(
            "second",
            "project-b",
            [@"C:\work\project-b"],
            [Terminal("two", @"C:\work\project-b")]);

        var selected = _registry.Select([first, second], "", @"C:\work\project-b", "project-b");

        Assert.True(selected.IsMatch);
        Assert.Equal("second", selected.Bridge!.BridgeId);
        Assert.Equal("exactTerminalWorkingDirectory", selected.MatchKind);
    }

    [Fact]
    public void Select_rejects_equal_confidence_matches()
    {
        var first = Bridge(
            "first",
            "project",
            [@"C:\work\project"],
            [Terminal("one", @"C:\work\project")]);
        var second = Bridge(
            "second",
            "project",
            [@"C:\work\project"],
            [Terminal("two", @"C:\work\project")]);

        var selected = _registry.Select([first, second], "", @"C:\work\project", "project");

        Assert.False(selected.IsMatch);
        Assert.Equal("ambiguous", selected.MatchKind);
    }

    [Fact]
    public void Select_does_not_match_broad_parent_directory_to_arbitrary_terminal()
    {
        var bridge = Bridge(
            "first",
            "project",
            [@"C:\work\project"],
            [Terminal("one", @"C:\work\project")]);

        var selected = _registry.Select([bridge], "", @"C:\Users\admin", "admin");

        Assert.False(selected.IsMatch);
        Assert.Equal("noMatch", selected.MatchKind);
    }

    [Fact]
    public void Select_reports_bridge_not_running_when_registry_is_empty()
    {
        var selected = _registry.Select([], "", @"C:\work\project", "project");

        Assert.False(selected.IsMatch);
        Assert.Equal("bridgeNotRunning", selected.MatchKind);
    }

    [Fact]
    public void Select_uses_terminal_token_when_working_directories_are_identical()
    {
        var bridge = Bridge(
            "first",
            "project",
            [@"C:\work\project"],
            [
                Terminal("one", @"C:\work\project", "token-one"),
                Terminal("two", @"C:\work\project", "token-two")
            ]);

        var selected = _registry.Select(
            [bridge],
            "token-two",
            @"C:\work\project",
            "project");

        Assert.True(selected.IsMatch);
        Assert.Equal("first", selected.Bridge!.BridgeId);
        Assert.Equal("terminalToken", selected.MatchKind);
    }

    [Fact]
    public void Select_does_not_fall_back_to_cwd_when_terminal_token_is_missing()
    {
        var bridge = Bridge(
            "first",
            "project",
            [@"C:\work\project"],
            [Terminal("one", @"C:\work\project", "different-token")]);

        var selected = _registry.Select(
            [bridge],
            "requested-token",
            @"C:\work\project",
            "project");

        Assert.False(selected.IsMatch);
        Assert.Equal("terminalTokenNotRegistered", selected.MatchKind);
    }

    private static TerminalBridgeRegistration Bridge(
        string id,
        string workspaceName,
        IReadOnlyList<string> workspaceFolders,
        IReadOnlyList<TerminalBridgeTerminal> terminals)
        => new(
            2,
            id,
            123,
            DateTimeOffset.UtcNow,
            workspaceName,
            workspaceFolders,
            terminals);

    private static TerminalBridgeTerminal Terminal(string name, string cwd, string token = "")
        => new($"{name}-id", token, name, 456, cwd);
}
