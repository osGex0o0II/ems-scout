namespace EmsScout.Tests;

public sealed class StartupOverlayUiContractTests
{
    [Fact]
    public void MainWindowProvidesAColdLaunchLogoOverlayWithoutTrayReplay()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "MainWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "App.xaml.cs"));

        Assert.Contains("x:Name=\"StartupOverlay\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml);
        Assert.Contains("x:Name=\"StartupLogo\"", xaml);
        Assert.Contains("EmsScoutLogo.png", xaml);
        Assert.Contains("PlayStartupAnimation", codeBehind);
        Assert.Contains("StartupAnimationCompleted", codeBehind);
        Assert.Contains("StartupOverlay.Visibility = Visibility.Collapsed", codeBehind);
        Assert.Contains("mainWindow.PlayStartupAnimation();", app);
        Assert.DoesNotContain("PlayStartupAnimation();", codeBehind[codeBehind.IndexOf("private void ShowFromTray", StringComparison.Ordinal)..]);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "native")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
