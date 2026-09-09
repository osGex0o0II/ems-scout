namespace EmsScout.Tests;

public sealed class DiagnosticsUiContractTests
{
    [Fact]
    public void AboutPageUsesCompactInformationSections()
    {
        var root = LocateRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "Pages",
            "DiagnosticsPage.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "MainWindow.xaml"));

        Assert.DoesNotContain("应用身份、版本与支持信息", xaml);
        Assert.DoesNotContain("空调设备采集与数据管理工具", xaml);
        Assert.DoesNotContain("用于反馈问题和核对运行环境。", xaml);
        Assert.DoesNotContain("EMS 空调控制台", xaml);
        Assert.Contains("关于 EMS Scout", xaml);
        Assert.Contains("Text=\"EMS Scout\"", xaml);
        Assert.DoesNotContain("系统区域", xaml);
        Assert.Contains("Text=\"系统时区\"", xaml);
        Assert.Contains("Text=\"Windows 版本\"", xaml);
        Assert.Contains("Text=\"处理器与内存\"", xaml);
        Assert.Contains("ViewModel.WindowsVersion, Mode=OneWay}\" TextWrapping=\"WrapWholeWords\"", xaml);
        Assert.DoesNotContain("EMS 空调控制台", mainWindow);
        Assert.Contains("Title=\"EMS Scout\"", mainWindow);
        Assert.Contains("Text=\"EMS Scout\"", mainWindow);
        Assert.Contains("Text=\"设备信息\"", xaml);
        Assert.Contains("ViewModel.CurrentSystemTime", xaml);
        Assert.Contains("ViewModel.SystemLanguage", xaml);
        Assert.Contains("ViewModel.DeviceName", xaml);
        var deviceGridStart = xaml.IndexOf("<Grid ColumnSpacing=\"16\" RowSpacing=\"10\">", StringComparison.Ordinal);
        var deviceGridEnd = xaml.IndexOf("</Grid>", deviceGridStart, StringComparison.Ordinal);
        Assert.True(deviceGridStart >= 0);
        Assert.True(deviceGridEnd > deviceGridStart);
        var deviceGrid = xaml[deviceGridStart..deviceGridEnd];
        Assert.Contains("<Grid.RowDefinitions>", deviceGrid);
        Assert.Equal(3, deviceGrid.Split("<RowDefinition Height=\"Auto\" />", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("{Binding Detail}", xaml);
    }

    [Fact]
    public void AboutViewModelExposesSystemAndDeviceDetails()
    {
        var root = LocateRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "native",
            "src",
            "EmsScout.Desktop",
            "ViewModels",
            "DiagnosticsViewModel.cs"));

        Assert.Contains("DeviceRows", viewModel);
        Assert.Contains("Environment.MachineName", viewModel);
        Assert.Contains("CultureInfo.CurrentUICulture", viewModel);
        Assert.Contains("DeviceRows.Select", viewModel);
        Assert.Contains("CurrentSystemTime", viewModel);
        Assert.Contains("TimeZoneInfo.Local", viewModel);
        Assert.Contains("GlobalMemoryStatusEx", viewModel);
        Assert.Contains("ProcessorNameString", viewModel);
        Assert.Contains("ProductName", viewModel);
        Assert.DoesNotContain("SystemRegion", viewModel);
        Assert.DoesNotContain("SystemArchitecture", viewModel);
        Assert.DoesNotContain("RuntimeInformation.OSArchitecture", viewModel);
        Assert.DoesNotContain("Environment.ProcessorCount", viewModel);
        Assert.Contains("timeZone.StandardName", viewModel);
        Assert.DoesNotContain("timeZone.DisplayName", viewModel);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "out")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
