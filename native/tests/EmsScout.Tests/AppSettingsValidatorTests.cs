using EmsScout.Application.Settings;

namespace EmsScout.Tests;

public sealed class AppSettingsValidatorTests
{
    [Theory]
    [InlineData("not-a-url", "EMS 地址")]
    [InlineData("ftp://example.local/ui", "EMS 地址")]
    [InlineData("http://user:secret@example.local/ui", "EMS 地址")]
    [InlineData("http://example.local/ui-malicious", "EMS 地址")]
    public void RejectsInvalidEmsUrl(string emsUrl, string expectedMessage)
    {
        var settings = new AppSettings { EmsUrl = emsUrl };

        var error = AppSettingsValidator.Validate(settings);

        Assert.NotNull(error);
        Assert.Contains(expectedMessage, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void RejectsInvalidCdpPort(int port)
    {
        var settings = new AppSettings { EdgeCdpPort = port };

        var error = AppSettingsValidator.Validate(settings);

        Assert.NotNull(error);
        Assert.Contains("1-65535", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonFiniteCdpPortInput()
    {
        Assert.Contains("1-65535", AppSettingsValidator.ValidateEdgeCdpPortInput(double.NaN), StringComparison.Ordinal);
        Assert.Contains("1-65535", AppSettingsValidator.ValidateEdgeCdpPortInput(double.PositiveInfinity), StringComparison.Ordinal);
        Assert.Contains("1-65535", AppSettingsValidator.ValidateEdgeCdpPortInput(double.NegativeInfinity), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9222.4)]
    [InlineData(65535)]
    public void AcceptsValidCdpPortInput(double port)
    {
        Assert.Null(AppSettingsValidator.ValidateEdgeCdpPortInput(port));
    }

    [Fact]
    public void RejectsMissingDirectories()
    {
        var dataError = AppSettingsValidator.Validate(new AppSettings { DataDirectory = "" });
        var exportError = AppSettingsValidator.Validate(new AppSettings { ExportDirectory = "" });

        Assert.Contains("数据目录", dataError, StringComparison.Ordinal);
        Assert.Contains("导出目录", exportError, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsValidSettings()
    {
        var settings = new AppSettings
        {
            EmsUrl = "https://example.local/ui",
            EdgeCdpPort = 9222,
            DataDirectory = "out",
            ExportDirectory = "out/data-management-export",
        };

        Assert.Null(AppSettingsValidator.Validate(settings));
    }

    [Fact]
    public void RejectsInvertedDashboardTemperatureRange()
    {
        var settings = new AppSettings
        {
            DashboardTemperatureMin = 28,
            DashboardTemperatureMax = 21,
        };

        var error = AppSettingsValidator.Validate(settings);

        Assert.Contains("温度", error, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceValidationUsesTheSamePathPolicyAsRuntimeResolution()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "ems-scout-validator-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(fixture, "workspace");
        var outside = Path.Combine(fixture, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        var file = Path.Combine(workspace, "file-not-directory");
        File.WriteAllText(file, "x");

        Assert.Contains("数据目录", AppSettingsValidator.Validate(
            new AppSettings { DataDirectory = outside }, workspace), StringComparison.Ordinal);
        Assert.Contains("导出目录", AppSettingsValidator.Validate(
            new AppSettings { ExportDirectory = file }, workspace), StringComparison.Ordinal);
        Assert.Null(AppSettingsValidator.Validate(new AppSettings
        {
            DataDirectory = "out",
            ExportDirectory = "out/exports",
        }, workspace));
        Directory.Delete(fixture, recursive: true);
    }
}
