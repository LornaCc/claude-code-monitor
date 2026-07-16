using CCMonitor.Core.Services;
using Xunit;

namespace CCMonitor.Core.Tests;

public sealed class SessionVisibilityStoreTests
{
    [Fact]
    public void Hidden_sessions_can_be_hidden_restored_and_restored_all()
    {
        using var temp = new TempDirectory();
        var store = new SessionVisibilityStore(new CcMonitorPaths(temp.Path));

        store.Hide("s1");
        store.Hide("s2");

        Assert.Contains("s1", store.LoadHidden());
        Assert.Contains("s2", store.LoadHidden());

        store.Restore("s1");
        Assert.DoesNotContain("s1", store.LoadHidden());
        Assert.Contains("s2", store.LoadHidden());

        store.RestoreAll();
        Assert.Empty(store.LoadHidden());
    }

    [Fact]
    public void Removed_session_is_tracked_and_removed_from_hidden()
    {
        using var temp = new TempDirectory();
        var store = new SessionVisibilityStore(new CcMonitorPaths(temp.Path));

        store.Hide("s1");
        store.RemovePermanently("s1");

        Assert.True(store.IsRemoved("s1"));
        Assert.DoesNotContain("s1", store.LoadHidden());
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cc-monitor-visibility-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
