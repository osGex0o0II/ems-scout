namespace EmsScout.Application.Settings;

public static class AppSettingsValidator
{
    public const string InvalidEdgeCdpPortMessage = "Edge CDP 端口必须在 1-65535 之间";

    public static string? Validate(AppSettings settings)
    {
        if (!Uri.TryCreate(settings.EmsUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "EMS 地址必须是 http 或 https 开头的完整地址";
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return "EMS 地址不能包含用户信息";
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

        return null;
    }

    public static string? ValidateEdgeCdpPortInput(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < 1 || value > 65535
            ? InvalidEdgeCdpPortMessage
            : null;
    }
}
