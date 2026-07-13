using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace EmsScout.Desktop.ViewModels;

public sealed class PreflightCheckRow(
    string title,
    string state,
    string detail,
    string glyph,
    bool isPassed = false,
    bool isBlocked = false)
{
    public string Title { get; } = title;

    public string State { get; } = state;

    public string Detail { get; } = detail;

    public string Glyph { get; } = glyph;

    public bool IsPassed { get; } = isPassed;

    public bool IsBlocked { get; } = isBlocked;

    public bool IsPending { get; } = !isPassed && !isBlocked;

    public Brush IconForeground { get; } = isPassed
        ? new SolidColorBrush(ColorHelper.FromArgb(255, 78, 105, 96))
        : isBlocked
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 128, 105, 76))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 107, 114, 128));

    public static PreflightCheckRow Pending(string title, string detail)
    {
        return new PreflightCheckRow(title, "待检", detail, "\uE9D9");
    }

    public static PreflightCheckRow Ok(string title, string detail)
    {
        return new PreflightCheckRow(title, "通过", detail, "\uE930", isPassed: true);
    }

    public static PreflightCheckRow Warning(string title, string detail)
    {
        return new PreflightCheckRow(title, "未通过", detail, "\uE7BA", isBlocked: true);
    }

    public static PreflightCheckRow Unknown(string title, string detail)
    {
        return new PreflightCheckRow(title, "未验证", detail, "\uE946");
    }
}
