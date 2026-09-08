namespace EmsScout.Application;

public static class TrayClosePolicy
{
    public static bool ShouldHideOnClose(bool trayEnabled, bool exitRequested) =>
        trayEnabled && !exitRequested;
}
