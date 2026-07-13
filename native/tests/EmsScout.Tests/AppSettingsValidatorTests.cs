using EmsScout.Application.Settings;

namespace EmsScout.Tests;

public sealed class AppSettingsValidatorTests
{
    [Theory]
    [InlineData("not-a-url", "EMS 地址")]
    [InlineData("ftp://example.local/ui", "EMS 地址")]
    [InlineData("https://user:secret@example.local/ui", "用户信息")]
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
}
