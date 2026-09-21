using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmsScout.Desktop;

public sealed class StartupFailureWindow : Window
{
    public StartupFailureWindow(Exception exception, Func<Task>? retry = null)
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
        var explanation = new TextBlock
        {
            Text = "SQLite 数据库迁移失败。为避免在未完成迁移的数据库上读取、修改或删除现场数据，正常业务界面已被阻断。",
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(explanation);
        var detailsText = new TextBlock
        {
            Text = details,
            MaxHeight = 240,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        panel.Children.Add(detailsText);
        if (retry is not null)
        {
            explanation.Text = "保存的数据目录不符合当前安全规则。可以恢复工作区内的默认数据和导出目录，然后重新执行完整的 SQLite 迁移。其他设置会保留。";
            var recover = new Button { Content = "恢复安全目录并重试" };
            recover.Click += async (_, _) =>
            {
                recover.IsEnabled = false;
                try
                {
                    await retry();
                    Close();
                }
                catch (Exception retryException)
                {
                    detailsText.Text = retryException.ToString();
                    recover.IsEnabled = true;
                }
            };
            panel.Children.Add(recover);
        }
        var close = new Button { Content = "关闭" };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
    }
}
