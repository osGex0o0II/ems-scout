using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmsScout.Desktop;

public sealed class StartupFailureWindow : Window
{
    public StartupFailureWindow(Exception exception)
    {
        Title = "EMS Scout - 启动失败";

        var details = exception.ToString();
        var panel = new StackPanel
        {
            Spacing = 16,
            Padding = new Thickness(32),
        };
        panel.Children.Add(new TextBlock
        {
            Text = "无法安全打开 EMS Scout",
            FontSize = 24,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "SQLite 数据库迁移失败。为避免在未完成迁移的数据库上读取、修改或删除现场数据，正常业务界面已被阻断。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = details,
            MaxHeight = 240,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        var close = new Button { Content = "关闭" };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
    }
}
