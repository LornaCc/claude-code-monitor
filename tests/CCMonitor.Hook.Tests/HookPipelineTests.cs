using CCMonitor.Core.HookEvents;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;
using Xunit;

namespace CCMonitor.Hook.Tests;

public sealed class HookPipelineTests
{
    [Fact]
    public async Task Mock_stdin_event_updates_state_file()
    {
        using var temp = new TempDirectory();
        var paths = new CcMonitorPaths(temp.Path);
        var config = new MonitorConfig();
        var store = new ClaudeSessionStateStore(paths);
        var parser = new HookEventParser();
        var machine = new ClaudeSessionStateMachine();

        var hookEvent = parser.Parse("""{"hook_event_name":"UserPromptSubmit","session_id":"s1","cwd":"C:\\work\\demo"}""");
        await store.WithSessionLockAsync(hookEvent.SessionId, async () =>
        {
            var state = await store.GetOrCreateAsync(hookEvent.SessionId, hookEvent.WorkingDirectory);
            machine.Apply(state, hookEvent, config);
            await store.SaveAtomicAsync(state);
        });

        var loaded = await store.LoadAllAsync();
        Assert.Single(loaded);
        Assert.Equal(ClaudeSessionStatus.Running, loaded[0].Status);
        Assert.Equal("demo", loaded[0].ProjectName);
    }

    [Fact]
    public async Task Terminal_token_survives_session_state_updates()
    {
        using var temp = new TempDirectory();
        var paths = new CcMonitorPaths(temp.Path);
        var store = new ClaudeSessionStateStore(paths);
        var machine = new ClaudeSessionStateMachine();
        var hookEvent = new HookEvent
        {
            Kind = HookEventKind.UserPromptSubmit,
            RawEventName = "UserPromptSubmit",
            SessionId = "new-session-id",
            TerminalToken = "0123456789abcdef0123456789abcdef",
            WorkingDirectory = @"C:\work\demo"
        };

        var state = await store.GetOrCreateAsync(hookEvent.SessionId, hookEvent.WorkingDirectory);
        machine.Apply(state, hookEvent, new MonitorConfig());
        await store.SaveAtomicAsync(state);

        var loaded = Assert.Single(await store.LoadAllAsync());
        Assert.Equal("new-session-id", loaded.SessionId);
        Assert.Equal("0123456789abcdef0123456789abcdef", loaded.TerminalToken);
    }

    [Fact]
    public async Task Session_lock_timeout_is_reported_instead_of_silently_dropping_event()
    {
        using var temp = new TempDirectory();
        var store = new ClaudeSessionStateStore(new CcMonitorPaths(temp.Path));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = store.WithSessionLockAsync("same-session", async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            store.WithSessionLockAsync("same-session", () => Task.CompletedTask, timeoutMs: 100));

        release.SetResult();
        await holder;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cc-monitor-hook-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
