namespace EmsScout.Tests;

public sealed class TrayUiContractTests
{
    [Fact]
    public void DesktopUsesWinUiExAndInterceptsWindowClose()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "EmsScout.Desktop.csproj"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("PackageReference Include=\"WinUIEx\"", project, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Closing +=", mainWindow, StringComparison.Ordinal);
        Assert.Contains("TrayIconContextMenu", mainWindow, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Hide()", mainWindow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
