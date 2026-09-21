namespace EmsScout.Application.Settings;

public static class AppSettingsValidator
{
    public const string InvalidEdgeCdpPortMessage = "Edge CDP 端口必须在 1-65535 之间";

    public static string? Validate(AppSettings settings)
    {
        if (!Uri.TryCreate(settings.EmsUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !IsEmsPath(uri.AbsolutePath))
        {
            return "EMS 地址必须是 http 或 https 开头的完整地址";
        }

        if (settings.EdgeCdpPort is < 1 or > 65535)
        {
            return InvalidEdgeCdpPortMessage;
        }

        if (string.IsNullOrWhiteSpace(settings.DataDirectory))
        {
            return "数据目录不能为空";
        }

        if (string.IsNullOrWhiteSpace(settings.ExportDirectory))
        {
            return "导出目录不能为空";
        }

        if (settings.DashboardTemperatureMin > settings.DashboardTemperatureMax)
        {
            return "总览温度下限不能大于上限";
        }

        if (!double.IsFinite(settings.DashboardTemperatureMin) ||
            !double.IsFinite(settings.DashboardTemperatureMax) ||
            settings.DashboardTemperatureMin is < 5 or > 40 ||
            settings.DashboardTemperatureMax is < 5 or > 40)
        {
            return "总览温度范围必须在 5-40℃ 之间";
        }

        return null;
    }

    public static string? Validate(AppSettings settings, string workspaceRoot)
    {
        var error = Validate(settings);
        if (error is not null)
        {
            return error;
        }

        return ValidateDirectories(settings, workspaceRoot);
    }

    public static string? ValidateDirectories(AppSettings settings, string workspaceRoot)
    {
        try
        {
            PathSafety.ResolveDirectory(workspaceRoot, settings.DataDirectory);
        }
        catch (Exception exception) when (IsPathValidationException(exception))
        {
            return $"数据目录无效：{exception.Message}";
        }

        try
        {
            PathSafety.ResolveDirectory(workspaceRoot, settings.ExportDirectory);
        }
        catch (Exception exception) when (IsPathValidationException(exception))
        {
            return $"导出目录无效：{exception.Message}";
        }

        return null;
    }

    private static bool IsPathValidationException(Exception exception) =>
        exception is InvalidOperationException or ArgumentException or NotSupportedException or
            IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static bool IsEmsPath(string path)
    {
        var normalized = (path ?? string.Empty).TrimEnd('/');
        return normalized.Equals("/ui", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("/ui/", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ValidateEdgeCdpPortInput(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 1 || value > 65535
            ? InvalidEdgeCdpPortMessage
            : null;
    }
}
