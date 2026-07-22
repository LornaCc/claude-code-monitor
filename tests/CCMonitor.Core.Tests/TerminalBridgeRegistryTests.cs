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
    public void Select_matches_terminal_parent_of_session_working_directory()
    {
        var expected = Bridge(
            "expected",
            "project",
            [@"C:\work\project"],
            [Terminal("project", @"C:\work\project")]);
        var unrelated = Bridge(
            "unrelated",
            "other",
            [@"C:\work\other"],
            [Terminal("other", @"C:\work\other")]);

        var selected = _registry.Select(
            [unrelated, expected],
            "",
            @"C:\work\project\src\feature",
            "project");

        Assert.True(selected.IsMatch);
        Assert.Equal("expected", selected.Bridge!.BridgeId);
        Assert.Equal("workingDirectoryUnderTerminal", selected.MatchKind);
    }

    [Fact]
    public void Select_prefers_closest_terminal_parent_across_windows()
    {
        var projectRoot = Bridge(
            "project-root",
            "project",
            [@"C:\work\project"],
            [Terminal("project", @"C:\work\project")]);
        var sourceRoot = Bridge(
            "source-root",
            "project-src",
            [@"C:\work\project"],
            [Terminal("source", @"C:\work\project\src")]);

        var selected = _registry.Select(
            [projectRoot, sourceRoot],
            "",
            @"C:\work\project\src\feature",
            "project");

        Assert.True(selected.IsMatch);
        Assert.Equal("source-root", selected.Bridge!.BridgeId);
        Assert.Equal("workingDirectoryUnderTerminal", selected.MatchKind);
    }

    [Fact]
    public void Select_rejects_equally_close_terminal_parents_across_windows()
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

        var selected = _registry.Select(
            [first, second],
            "",
            @"C:\work\project\src",
            "project");

        Assert.False(selected.IsMatch);
        Assert.Equal("ambiguous", selected.MatchKind);
    }

    [Fact]
    public void Select_does_not_treat_broad_terminal_parent_as_terminal_match()
    {
        var bridge = Bridge(
            "first",
            "project",
            [@"C:\Users\admin\Desktop\projects\project"],
            [Terminal("desktop", @"C:\Users\admin\Desktop")]);

        var selected = _registry.Select(
            [bridge],
            "",
            @"C:\Users\admin\Desktop\projects\project\src",
            "project");

        Assert.True(selected.IsMatch);
        Assert.Equal("workingDirectoryInWorkspace", selected.MatchKind);
    }

    [Fact]
    public void Select_allows_specific_terminal_parent_when_window_has_no_workspace()
    {
        var bridge = Bridge(
            "first",
            "",
            [],
            [Terminal("project", @"C:\Users\admin\project")]);

        var selected = _registry.Select(
            [bridge],
            "",
            @"C:\Users\admin\project\src",
            "src");

        Assert.True(selected.IsMatch);
        Assert.Equal("workingDirectoryUnderTerminal", selected.MatchKind);
    }

    [Fact]
    public void Select_allows_nearby_project_terminal_outside_window_workspace()
    {
        var bridge = Bridge(
            "first",
            "fstr_img_tag_manager",
            [@"C:\Users\admin\Desktop\2026Intern\fstr_img_tag_manager"],
            [Terminal("clearMLdemo", @"C:\Users\admin\Desktop\2026Intern\clearMLdemo")]);

        var selected = _registry.Select(
            [bridge],
            "",
            @"C:\Users\admin\Desktop\2026Intern\clearMLdemo\clearml\clearml",
            "clearml");

        Assert.True(selected.IsMatch);
        Assert.Equal("workingDirectoryUnderTerminal", selected.MatchKind);
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

    [Fact]
    public void Select_uses_manual_process_binding_before_cwd()
    {
        var expected = Bridge(
            "expected",
            "other-workspace",
            [@"C:\work\other"],
            [Terminal("bound", @"C:\unrelated") with { ProcessId = 9001 }]);
        var cwdMatch = Bridge(
            "cwd-match",
            "project",
            [@"C:\work\project"],
            [Terminal("cwd", @"C:\work\project")]);
        var binding = new ManualTerminalBinding(
            "session",
            "",
            9001,
            "bound",
            @"C:\unrelated",
            DateTimeOffset.UtcNow);

        var selected = _registry.Select(
            [cwdMatch, expected],
            "",
            @"C:\work\project",
            "project",
            binding);

        Assert.True(selected.IsMatch);
        Assert.Equal("expected", selected.Bridge!.BridgeId);
        Assert.Equal("manualBinding", selected.MatchKind);
    }

    [Fact]
    public void Select_uses_the_window_where_a_duplicated_bound_terminal_is_active()
    {
        var activeOwner = Bridge(
            "active-owner",
            "",
            [],
            [Terminal("bound", @"C:\work\project") with { ProcessId = 9001 }]) with
        {
            ActiveTerminalProcessId = 9001
        };
        var staleOwner = Bridge(
            "stale-owner",
            "project",
            [@"C:\work\project"],
            [
                Terminal("bound", @"C:\work\project") with { ProcessId = 9001 },
                Terminal("other", @"C:\work\project") with { ProcessId = 9002 }
            ]) with
        {
            ActiveTerminalProcessId = 9002
        };
        var binding = new ManualTerminalBinding(
            "session",
            "",
            9001,
            "bound",
            @"C:\work\project",
            DateTimeOffset.UtcNow);

        var selected = _registry.Select(
            [staleOwner, activeOwner],
            "",
            @"C:\work\project",
            "project",
            binding);

        Assert.True(selected.IsMatch);
        Assert.Equal("active-owner", selected.Bridge!.BridgeId);
        Assert.Equal("manualBindingActiveTerminal", selected.MatchKind);
    }

    [Fact]
    public void Select_uses_explicit_bridge_when_a_bound_terminal_is_duplicated()
    {
        var oldWindow = Bridge(
            "old-window",
            "project",
            [@"C:\work\project"],
            [Terminal("bound", @"C:\work\project") with { ProcessId = 9001 }]);
        var newWindow = Bridge(
            "new-window",
            "",
            [],
            [Terminal("bound", @"C:\work\project") with { ProcessId = 9001 }]);
        var binding = new ManualTerminalBinding(
            "session",
            "",
            9001,
            "bound",
            @"C:\work\project",
            DateTimeOffset.UtcNow,
            "new-window");

        var selected = _registry.Select(
            [oldWindow, newWindow],
            "",
            @"C:\work\project",
            "project",
            binding);

        Assert.True(selected.IsMatch);
        Assert.Equal("new-window", selected.Bridge!.BridgeId);
        Assert.Equal("manualBindingBridge", selected.MatchKind);
    }

    [Fact]
    public void Select_uses_observed_terminal_process_id_before_cwd()
    {
        var expected = Bridge(
            "expected",
            "",
            [],
            [Terminal("bound", @"C:\unrelated") with { ProcessId = 9001 }]);
        var cwdMatch = Bridge(
            "cwd-match",
            "project",
            [@"C:\work\project"],
            [Terminal("cwd", @"C:\work\project")]);

        var selected = _registry.Select(
            [cwdMatch, expected],
            "",
            @"C:\work\project",
            "project",
            preferredTerminalProcessId: 9001);

        Assert.True(selected.IsMatch);
        Assert.Equal("expected", selected.Bridge!.BridgeId);
        Assert.Equal("terminalProcessId", selected.MatchKind);
    }

    [Fact]
    public void Select_falls_back_to_cwd_when_observed_process_id_is_stale()
    {
        var bridge = Bridge(
            "cwd-match",
            "project",
            [@"C:\work\project"],
            [Terminal("cwd", @"C:\work\project")]);

        var selected = _registry.Select(
            [bridge],
            "",
            @"C:\work\project",
            "project",
            preferredTerminalProcessId: 9001);

        Assert.True(selected.IsMatch);
        Assert.Equal("cwd-match", selected.Bridge!.BridgeId);
        Assert.Equal("exactTerminalWorkingDirectory", selected.MatchKind);
    }

    [Fact]
    public void Select_reports_stale_manual_binding_instead_of_guessing_by_cwd()
    {
        var bridge = Bridge(
            "cwd-match",
            "project",
            [@"C:\work\project"],
            [Terminal("cwd", @"C:\work\project")]);
        var binding = new ManualTerminalBinding(
            "session",
            "",
            9001,
            "missing",
            @"C:\unrelated",
            DateTimeOffset.UtcNow);

        var selected = _registry.Select(
            [bridge],
            "",
            @"C:\work\project",
            "project",
            binding);

        Assert.False(selected.IsMatch);
        Assert.Equal("manualTerminalNotRegistered", selected.MatchKind);
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
