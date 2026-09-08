namespace EmsScout.Tests;

public sealed class AboutPageUiContractTests
{
    [Fact]
    public void AboutPageProvidesAResponsiveHeroAndWorkingRepositoryActions()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "native", "src", "EmsScout.Desktop", "Pages", "DiagnosticsPage.xaml"));
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
