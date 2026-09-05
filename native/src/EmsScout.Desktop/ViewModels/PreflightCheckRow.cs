namespace EmsScout.Desktop.ViewModels;

public sealed class PreflightCheckRow(
    string title,
    string state,
    string detail,
    string glyph)
{
    public string Title { get; } = title;

    public string State { get; } = state;

    public string Detail { get; } = detail;

    public string Glyph { get; } = glyph;

    public static PreflightCheckRow Pending(string title, string detail)
    {
        return new PreflightCheckRow(title, "待检", detail, "\uE9D9");
    }

    public static PreflightCheckRow Ok(string title, string detail)
    {
        return new PreflightCheckRow(title, "通过", detail, "\uE930");
    }

    public static PreflightCheckRow Warning(string title, string detail)
    {
        return new PreflightCheckRow(title, "注意", detail, "\uE7BA");
    }

    public static PreflightCheckRow Unknown(string title, string detail)
    {
        return new PreflightCheckRow(title, "未验证", detail, "\uE946");
    }
}
