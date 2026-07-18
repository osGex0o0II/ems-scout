using System.Net;
using System.Text;
using EmsScout.Application.Updates;

namespace EmsScout.Tests;

public sealed class AppUpdateServiceTests
{
    private static readonly Uri ManifestUri = new(
        "https://github.com/osGex0o0II/ems-scout/releases/latest/download/EmsScout.appinstaller");

    [Fact]
    public async Task CheckAsyncReturnsAvailableWhenManifestVersionIsNewer()
    {
        var service = CreateService(AppInstallerManifestParserTests.ValidManifest(), new Version(1, 2, 3, 3));

        var result = await service.CheckAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(1, 2, 3, 3), result.CurrentVersion);
        Assert.Equal(new Version(1, 2, 3, 4), result.AvailableVersion);
        Assert.Equal(ManifestUri, result.AppInstallerUri);
    }

    [Fact]
    public async Task CheckAsyncReturnsCurrentWhenVersionsAreEqual()
    {
        var service = CreateService(AppInstallerManifestParserTests.ValidManifest(), new Version(1, 2, 3, 4));

        var result = await service.CheckAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(result.CurrentVersion, result.AvailableVersion);
    }

    [Theory]
    [InlineData("WRONG", "CN=EMS Scout", "https://github.com/osGex0o0II/ems-scout/releases/download/v1.2.3.4/EmsScout.msix", "identity")]
    [InlineData("1FACE092-146B-4AE5-83DB-3990E6AE8371", "CN=Other", "https://github.com/osGex0o0II/ems-scout/releases/download/v1.2.3.4/EmsScout.msix", "publisher")]
    [InlineData("1FACE092-146B-4AE5-83DB-3990E6AE8371", "CN=EMS Scout", "http://github.com/osGex0o0II/ems-scout/releases/download/v1.2.3.4/EmsScout.msix", "HTTPS")]
    [InlineData("1FACE092-146B-4AE5-83DB-3990E6AE8371", "CN=EMS Scout", "https://example.com/EmsScout.msix", "host")]
    public async Task CheckAsyncRejectsUntrustedManifestFields(
        string packageName,
        string publisher,
        string packageUri,
        string expectedMessage)
    {
        var xml = AppInstallerManifestParserTests.ValidManifest(packageName, publisher, packageUri: packageUri);
        var service = CreateService(xml, new Version(1, 0, 0, 0));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsyncRejectsManifestLargerThanConfiguredLimit()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[257]),
        };
        var service = CreateService(response, new Version(1, 0, 0, 0), maxManifestBytes: 256);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());

        Assert.Contains("size", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsyncDelegatesOnlyTheConfiguredAppInstallerUri()
    {
        var launcher = new RecordingLauncher();
        var service = CreateService(
            AppInstallerManifestParserTests.ValidManifest(),
            new Version(1, 0, 0, 0),
            launcher);

        var launched = await service.InstallAsync();

        Assert.True(launched);
        Assert.Equal(ManifestUri, launcher.LaunchedUri);
    }

    private static AppUpdateService CreateService(
        string xml,
        Version currentVersion,
        RecordingLauncher? launcher = null,
        int maxManifestBytes = 256 * 1024)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/appinstaller"),
        };
        return CreateService(response, currentVersion, launcher, maxManifestBytes);
    }

    private static AppUpdateService CreateService(
        HttpResponseMessage response,
        Version currentVersion,
        RecordingLauncher? launcher = null,
        int maxManifestBytes = 256 * 1024)
    {
        var client = new HttpClient(new StaticResponseHandler(response));
        var options = new AppUpdateOptions(
            ManifestUri,
            "1FACE092-146B-4AE5-83DB-3990E6AE8371",
            "CN=EMS Scout",
            ["github.com"],
            maxManifestBytes);
        return new AppUpdateService(
            client,
            new FixedVersionProvider(currentVersion),
            launcher ?? new RecordingLauncher(),
            options);
    }

    private sealed class FixedVersionProvider(Version currentVersion) : IAppVersionProvider
    {
        public Version CurrentVersion { get; } = currentVersion;
    }

    private sealed class RecordingLauncher : IAppUpdateLauncher
    {
        public Uri? LaunchedUri { get; private set; }

        public Task<bool> LaunchAsync(Uri appInstallerUri, CancellationToken cancellationToken = default)
        {
            LaunchedUri = appInstallerUri;
            return Task.FromResult(true);
        }
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
