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

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cc-monitor-hook-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
