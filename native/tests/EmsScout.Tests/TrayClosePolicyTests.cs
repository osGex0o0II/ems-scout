using EmsScout.Application;

namespace EmsScout.Tests;

public sealed class TrayClosePolicyTests
{
    [Fact]
    public void WindowCloseIsRedirectedToTrayWhenTrayIsEnabled()
    {
        Assert.True(TrayClosePolicy.ShouldHideOnClose(trayEnabled: true, exitRequested: false));
    }

    [Fact]
    public void WindowCloseIsAllowedWhenExitWasRequested()
    {
        Assert.False(TrayClosePolicy.ShouldHideOnClose(trayEnabled: true, exitRequested: true));
    }

    [Fact]
    public void WindowCloseIsAllowedWhenTrayIsDisabled()
    {
        Assert.False(TrayClosePolicy.ShouldHideOnClose(trayEnabled: false, exitRequested: false));
    }
}
