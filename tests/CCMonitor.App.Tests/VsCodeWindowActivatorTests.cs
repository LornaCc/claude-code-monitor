using CCMonitor.App;
using Xunit;

namespace CCMonitor.App.Tests;

public sealed class VsCodeWindowActivatorTests
{
    [Theory]
    [InlineData(false, false, "normalOrArranged")]
    [InlineData(false, true, "maximized")]
    public void CreateActivationPlan_DoesNotRestoreVisibleWindow(
        bool isMinimized,
        bool isMaximized,
        string expectedState)
    {
        var plan = VsCodeWindowActivator.CreateActivationPlan(isMinimized, isMaximized);

        Assert.Equal(expectedState, plan.InitialWindowState);
        Assert.False(plan.RestoreBeforeActivation);
    }

    [Fact]
    public void CreateActivationPlan_RestoresOnlyMinimizedWindow()
    {
        var plan = VsCodeWindowActivator.CreateActivationPlan(isMinimized: true, isMaximized: false);

        Assert.Equal("minimized", plan.InitialWindowState);
        Assert.True(plan.RestoreBeforeActivation);
    }
}
