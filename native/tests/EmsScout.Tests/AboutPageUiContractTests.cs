namespace EmsScout.Tests;

public sealed class AboutPageUiContractTests
{
    [Fact]
    public void AboutPageProvidesAResponsiveHeroAndWorkingRepositoryActions()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DiagnosticsPage.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "ViewModels", "DiagnosticsViewModel.cs"));

        Assert.Contains("<ScrollViewer", xaml);
        Assert.Contains("x:Name=\"HeroSection\"", xaml);
        Assert.Contains("打开 GitHub 仓库", xaml);
        Assert.Contains("复制仓库地址", xaml);
        Assert.Contains("复制诊断信息", xaml);
        Assert.DoesNotContain("感谢使用 EMS 空调控制台", xaml);
        Assert.Contains("RepositoryUrl", viewModel);
        Assert.Contains("OpenRepository", viewModel);
        Assert.Contains("CopyRepository", viewModel);
        Assert.Contains("CopyDiagnostics", viewModel);
        Assert.Contains("osGex0o0II", viewModel);
        Assert.Contains("Codex", viewModel);
        Assert.Contains("关于 EMS Scout", xaml);
        Assert.DoesNotContain("EMS 空调控制台", xaml);
        Assert.Contains("ms-appx:///Assets/EmsScoutLogo.png", xaml);
        Assert.DoesNotContain("Glyph=\"&#xE946;\"", xaml);
        Assert.Contains("EmsScoutLogo.png", mainWindow);

        var logoPath = Path.Combine(root, "native", "src", "EmsScout.Desktop", "Assets", "EmsScoutLogo.png");
        Assert.True(File.Exists(logoPath));
        var logoHeader = File.ReadAllBytes(logoPath);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, logoHeader[..8]);
        Assert.Equal(410, ReadPngInt32(logoHeader, 16));
        Assert.Equal(390, ReadPngInt32(logoHeader, 20));
    }

    private static int ReadPngInt32(byte[] bytes, int offset) =>
        (bytes[offset] << 24) |
        (bytes[offset + 1] << 16) |
        (bytes[offset + 2] << 8) |
        bytes[offset + 3];

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
