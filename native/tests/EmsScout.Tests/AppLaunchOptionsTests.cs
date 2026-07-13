using System.Text;
using EmsScout.Application.Settings;

namespace EmsScout.Tests;

public sealed class AppLaunchOptionsTests
{
    [Fact]
    public void ParsesAnEncodedIsolatedSettingsPath()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), "EMS Scout validation", "settings.json");
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(settingsPath));

        var options = AppLaunchOptions.Parse(AppLaunchOptions.SettingsPathArgumentPrefix + encodedPath);

        Assert.Equal(Path.GetFullPath(settingsPath), options.SettingsPathOverride);
    }

    [Fact]
    public void MissingOverrideKeepsNormalSettingsResolution()
    {
        var options = AppLaunchOptions.Parse(string.Empty);

        Assert.Null(options.SettingsPathOverride);
    }

    [Theory]
    [InlineData("--ui-validation-settings-base64=")]
    [InlineData("--ui-validation-settings-base64=not-base64")]
    public void InvalidOverrideFailsClosed(string arguments)
    {
        Assert.Throws<InvalidDataException>(() => AppLaunchOptions.Parse(arguments));
    }
}
