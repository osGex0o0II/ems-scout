using System.Text;

namespace EmsScout.Application.Updates;

public sealed class AppUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly IAppVersionProvider _versionProvider;
    private readonly IAppUpdateLauncher _launcher;
    private readonly AppUpdateOptions _options;

    public AppUpdateService(
        HttpClient httpClient,
        IAppVersionProvider versionProvider,
        IAppUpdateLauncher launcher,
        AppUpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(versionProvider);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.ManifestUri.IsAbsoluteUri ||
            !string.Equals(options.ManifestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Update manifest URI must use HTTPS.", nameof(options));
        }

        if (options.MaxManifestBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Manifest size limit must be positive.");
        }

        _httpClient = httpClient;
        _versionProvider = versionProvider;
        _launcher = launcher;
        _options = options;
    }

    public async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient
            .GetAsync(_options.ManifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > _options.MaxManifestBytes)
        {
            throw new InvalidDataException("Update manifest size exceeds the configured limit.");
        }

        var xml = await ReadManifestAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var manifest = AppInstallerManifestParser.Parse(xml);
        ValidateManifest(manifest);

        var currentVersion = _versionProvider.CurrentVersion;
        return new AppUpdateCheckResult(
            currentVersion,
            manifest.Version,
            manifest.Version > currentVersion,
            _options.ManifestUri);
    }

    public Task<bool> InstallAsync(CancellationToken cancellationToken = default) =>
        _launcher.LaunchAsync(_options.ManifestUri, cancellationToken);

    private async Task<string> ReadManifestAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > _options.MaxManifestBytes)
            {
                throw new InvalidDataException("Update manifest size exceeds the configured limit.");
            }

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private void ValidateManifest(AppInstallerManifest manifest)
    {
        if (manifest.AppInstallerUri != _options.ManifestUri)
        {
            throw new InvalidDataException("Update manifest self URI does not match the configured source.");
        }

        if (!string.Equals(
                manifest.PackageName,
                _options.ExpectedPackageName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update package identity does not match this application.");
        }

        if (!string.Equals(
                manifest.Publisher,
                _options.ExpectedPublisher,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update package publisher does not match this application.");
        }

        if (!string.Equals(manifest.PackageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update package URI must use HTTPS.");
        }

        if (!_options.AllowedPackageHosts.Contains(
                manifest.PackageUri.IdnHost,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update package host is not allowed.");
        }
    }
}
