using System.Diagnostics;
using System.Text.Json;

namespace EmsScout.Infrastructure.Sidecar;

public sealed record CollectionEnvironmentProbeRequest(
    string RuntimePath,
    string ApplicationRoot,
    string DataDirectory,
    int EdgeCdpPort,
    string EmsUrl);

public sealed record CollectionEnvironmentProbeResult(
    string NodeVersion,
    string NodeDependencies,
    bool NodeModulesPresent,
    bool EnumerationSidecarReady,
    bool RealtimeScriptReady,
    bool RealtimeAuditScriptReady,
    bool DatabaseReady,
    bool SnapshotReady,
    bool EmsUrlReady,
    EdgeCdpStatus Cdp);

public sealed record EdgeCdpStatus(
    bool IsReachable,
    int EmsPageCount,
    string Detail,
    string LoginDetail);

public sealed class CollectionEnvironmentProbe
{
    public async Task<CollectionEnvironmentProbeResult> ProbeAsync(
        CollectionEnvironmentProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        var nodeModules = Directory.Exists(Path.Combine(request.ApplicationRoot, "node_modules"));
        var enumerationSidecar =
            File.Exists(Path.Combine(request.ApplicationRoot, "src", "enumerate.js")) &&
            File.Exists(Path.Combine(request.ApplicationRoot, "sidecar", "collect.js")) &&
            File.Exists(Path.Combine(request.ApplicationRoot, "sidecar", "snapshot-adapter.js"));
        var nodeVersion = await ReadNodeVersionAsync(
            request.RuntimePath,
            request.ApplicationRoot,
            cancellationToken).ConfigureAwait(false);
        var nodeDependencies = await CheckNodeDependenciesAsync(
            request.RuntimePath,
            request.ApplicationRoot,
            cancellationToken).ConfigureAwait(false);
        var cdp = await CheckEdgeCdpAsync(
            request.EdgeCdpPort,
            request.EmsUrl,
            cancellationToken).ConfigureAwait(false);

        return new CollectionEnvironmentProbeResult(
            NodeVersion: nodeVersion,
            NodeDependencies: nodeDependencies,
            NodeModulesPresent: nodeModules,
            EnumerationSidecarReady: enumerationSidecar,
            RealtimeScriptReady: File.Exists(Path.Combine(
                request.ApplicationRoot,
                "scripts",
                "collect-realtime-all-batch.js")),
            RealtimeAuditScriptReady: File.Exists(Path.Combine(
                request.ApplicationRoot,
                "scripts",
                "audit-realtime-data.js")),
            DatabaseReady: File.Exists(Path.Combine(request.DataDirectory, "ac.db")),
            SnapshotReady: File.Exists(Path.Combine(request.DataDirectory, "collection_snapshot_v1.json")),
            EmsUrlReady: Uri.TryCreate(request.EmsUrl, UriKind.Absolute, out _),
            Cdp: cdp);
    }

    public async Task<EdgeCdpStatus> CheckEdgeCdpAsync(
        int port,
        string emsUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client
                .GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Unreachable(port);
            }

            try
            {
                using var pagesResponse = await client
                    .GetAsync($"http://127.0.0.1:{port}/json/list", cancellationToken)
                    .ConfigureAwait(false);
                if (!pagesResponse.IsSuccessStatusCode)
                {
                    return ListUnavailable(port);
                }

                await using var stream = await pagesResponse.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var document = await JsonDocument
                    .ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var pages = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray().Select(ReadCdpPage).ToArray()
                    : [];
                var emsPages = pages.Where(page => MatchesEmsPage(page.Url, emsUrl)).ToArray();
                if (emsPages.Length == 0)
                {
                    return new EdgeCdpStatus(
                        true,
                        0,
                        $"{port} 可访问；未发现 EMS 标签页",
                        "未发现 EMS 页面；请先在 Edge 中打开并登录 EMS");
                }

                return new EdgeCdpStatus(
                    true,
                    emsPages.Length,
                    $"{port} 可访问；发现 {emsPages.Length} 个 EMS 标签页",
                    $"发现 EMS 页面：{ValueOrDash(emsPages[0].Title)}。预检不能证明已登录，采集/验证会二次检查登录态");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ListUnavailable(port);
            }
            catch (HttpRequestException)
            {
                return ListUnavailable(port);
            }
            catch (JsonException)
            {
                return ListUnavailable(port);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable(port);
        }
        catch (HttpRequestException)
        {
            return Unreachable(port);
        }
    }

    public static bool MatchesEmsPage(string pageUrl, string emsUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl))
        {
            return false;
        }

        try
        {
            var expected = new Uri(emsUrl);
            var current = new Uri(pageUrl);
            return string.Equals(current.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
                   (current.AbsolutePath.Contains("/ui", StringComparison.OrdinalIgnoreCase) ||
                    expected.AbsolutePath.Contains(current.AbsolutePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (UriFormatException)
        {
            return pageUrl.Contains("172.29.248.4", StringComparison.OrdinalIgnoreCase) ||
                   pageUrl.Contains("/ui", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<string> ReadNodeVersionAsync(
        string runtimePath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = CreateNodeStartInfo(runtimePath, workingDirectory);
            startInfo.ArgumentList.Add("--version");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "不可用";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return "检查超时";
            }

            var output = await outputTask.ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Trim() : "不可用";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "不可用";
        }
    }

    private static async Task<string> CheckNodeDependenciesAsync(
        string runtimePath,
        string applicationRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = CreateNodeStartInfo(runtimePath, applicationRoot);
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("require('playwright'); console.log('ok')");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "不可用";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return "检查超时";
            }

            if (process.ExitCode == 0)
            {
                return "可用";
            }

            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(error) ? "加载失败" : error.Trim().Split(Environment.NewLine)[0];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "不可用";
        }
    }

    private static ProcessStartInfo CreateNodeStartInfo(string runtimePath, string workingDirectory)
    {
        return new ProcessStartInfo
        {
            FileName = runtimePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    private static EdgeCdpStatus Unreachable(int port)
    {
        return new(false, 0, $"{port} 未就绪", "CDP 未就绪，无法核实 EMS 页面");
    }

    private static EdgeCdpStatus ListUnavailable(int port)
    {
        return new(true, 0, $"{port} 可访问；页面列表读取失败", "只能证明 CDP 可达，不能证明 EMS 已登录");
    }

    private static CdpPageInfo ReadCdpPage(JsonElement element)
    {
        return new(
            Url: element.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
            Title: element.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty);
    }

    private static string ValueOrDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed record CdpPageInfo(string Url, string Title);
}
