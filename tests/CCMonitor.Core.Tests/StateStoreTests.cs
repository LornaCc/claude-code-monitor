using CCMonitor.Core.Models;
using CCMonitor.Core.Services;
using Xunit;

namespace CCMonitor.Core.Tests;

public sealed class StateStoreTests
{
    [Fact]
    public async Task Save_and_load_round_trips_state()
    {
        using var temp = new TempDirectory();
        var store = new ClaudeSessionStateStore(new CcMonitorPaths(temp.Path));
        var state = new ClaudeSessionState
        {
            SessionId = "abc123",
            ProjectName = "project",
            WorkingDirectory = @"C:\work\project",
            Status = ClaudeSessionStatus.Running,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };

        await store.SaveAtomicAsync(state);
        var loaded = await store.LoadAllAsync();

        Assert.Single(loaded);
        Assert.Equal(ClaudeSessionStatus.Running, loaded[0].Status);
    }

    [Fact]
    public async Task Corrupted_json_is_ignored()
    {
        using var temp = new TempDirectory();
        var paths = new CcMonitorPaths(temp.Path);
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(Path.Combine(paths.SessionsDirectory, "bad.json"), "{bad-json");

        var loaded = await new ClaudeSessionStateStore(paths).LoadAllAsync();

        Assert.Empty(loaded);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cc-monitor-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
