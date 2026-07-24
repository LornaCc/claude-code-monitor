using CCMonitor.App;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;
using System.Text.Json;
using Xunit;

namespace CCMonitor.App.Tests;

public sealed class VsCodeTerminalBridgeTests
{
    [Fact]
    public void Matched_terminal_in_a_new_window_refreshes_the_binding()
    {
        var now = DateTimeOffset.UtcNow;
        var binding = Binding("old-bridge");
        var result = Result(TerminalFocusStatus.Matched) with
        {
            BridgeId = "new-bridge",
            TerminalProcessId = 5128,
            TerminalName = "bash moved",
            TerminalToken = "new-token"
        };

        var refreshed = VsCodeTerminalBridge.CreateRefreshedBinding(
            binding,
            result,
            "new-bridge",
            @"C:\work\dev3",
            now);

        Assert.NotNull(refreshed);
        Assert.Equal("new-bridge", refreshed.BridgeId);
        Assert.Equal(5128, refreshed.TerminalProcessId);
        Assert.Equal("bash moved", refreshed.TerminalName);
        Assert.Equal("new-token", refreshed.TerminalToken);
        Assert.Equal(now, refreshed.UpdatedAtUtc);
    }

    [Fact]
    public void Failed_focus_does_not_refresh_the_binding()
    {
        var refreshed = VsCodeTerminalBridge.CreateRefreshedBinding(
            Binding("old-bridge"),
            Result(TerminalFocusStatus.NoMatch),
            "new-bridge",
            @"C:\work\dev3",
            DateTimeOffset.UtcNow);

        Assert.Null(refreshed);
    }

    [Fact]
    public void Unchanged_successful_binding_is_not_rewritten()
    {
        var binding = Binding("same-bridge");
        var result = Result(TerminalFocusStatus.Matched) with
        {
            BridgeId = "same-bridge",
            TerminalProcessId = binding.TerminalProcessId,
            TerminalName = binding.TerminalName,
            TerminalToken = binding.TerminalToken
        };

        var refreshed = VsCodeTerminalBridge.CreateRefreshedBinding(
            binding,
            result,
            "same-bridge",
            binding.WorkingDirectory,
            DateTimeOffset.UtcNow);

        Assert.Null(refreshed);
    }

    [Fact]
    public async Task Matched_bridge_response_persists_the_moved_terminal_window()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cc-monitor-app-tests-{Guid.NewGuid():N}");
        var paths = new CcMonitorPaths(root);
        paths.EnsureDirectories();

        try
        {
            var bindingStore = new ManualTerminalBindingStore(paths);
            bindingStore.Save(Binding("old-bridge"));
            var registration = new TerminalBridgeRegistration(
                3,
                "new-bridge",
                1001,
                DateTimeOffset.UtcNow,
                "dev3",
                [@"C:\work\dev3"],
                [
                    new TerminalBridgeTerminal(
                        "token:old-token",
                        "old-token",
                        "bash moved",
                        31580,
                        @"C:\work\dev3")
                ],
                31580,
                true);
            await File.WriteAllTextAsync(
                Path.Combine(paths.TerminalBridgesDirectory, "new-bridge.json"),
                JsonSerializer.Serialize(registration));

            var bridge = new VsCodeTerminalBridge(paths);
            var focusTask = bridge.RequestFocusAsync(
                "session-one",
                "",
                null,
                @"C:\work\dev3",
                "dev3",
                TimeSpan.FromSeconds(3));
            var requestPath = Path.Combine(root, "focus-terminal.json");
            var requestId = await WaitForRequestIdAsync(requestPath);
            var resultPath = Path.Combine(root, "focus-terminal-result.json");
            await File.WriteAllTextAsync(
                resultPath,
                JsonSerializer.Serialize(new
                {
                    protocolVersion = 3,
                    requestId,
                    completedAtUtc = DateTimeOffset.UtcNow,
                    status = "matched",
                    terminalProcessId = 31580,
                    terminalName = "bash moved",
                    workspaceName = "dev3",
                    matchKind = "manualBinding",
                    reason = "Focused moved terminal.",
                    bridgeId = "new-bridge",
                    terminalToken = "old-token"
                }));

            var result = await focusTask;
            var persisted = bindingStore.TryLoad("session-one");

            Assert.Equal(TerminalFocusStatus.Matched, result.Status);
            Assert.Equal("updated:old-bridge->new-bridge", result.BindingRefresh);
            Assert.NotNull(persisted);
            Assert.Equal("new-bridge", persisted.BridgeId);
            Assert.Equal(31580, persisted.TerminalProcessId);
            Assert.Equal("bash moved", persisted.TerminalName);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<string> WaitForRequestIdAsync(string requestPath)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(requestPath))
            {
                using var document = JsonDocument.Parse(
                    await File.ReadAllTextAsync(requestPath));
                return document.RootElement.GetProperty("requestId").GetString()!;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The terminal focus request was not written.");
    }

    private static ManualTerminalBinding Binding(string bridgeId)
        => new(
            "session-one",
            "old-token",
            31580,
            "bash",
            @"C:\work\dev3",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            bridgeId);

    private static TerminalFocusResult Result(TerminalFocusStatus status)
        => new(
            "request-one",
            DateTimeOffset.UtcNow,
            status,
            null,
            "",
            "dev3",
            "manualBinding",
            "",
            "");
}
