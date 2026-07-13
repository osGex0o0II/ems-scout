using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using EmsScout.Desktop.Pages;
using EmsScout.Desktop.Services;
using EmsScout.Infrastructure.Errors;

namespace EmsScout.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow(string? startupFailure = null)
    {
        InitializeComponent();

        AppWindow.SetIcon("Assets/AppIcon.ico");
        App.Services.GetRequiredService<WindowHandleProvider>().Attach(this);
        App.Services.GetRequiredService<AppUiSettingsService>().ApplyTheme(RootGrid);
        App.Services.GetRequiredService<NavigationService>().Attach(NavigateToData, NavigateToAudit, NavigateToDates);
        NavFrame.Navigate(typeof(HomePage));
        if (!string.IsNullOrWhiteSpace(startupFailure))
        {
            ShowStartupFailure(startupFailure);
        }
    }

    private async void RetryStartupMigration_Click(object sender, RoutedEventArgs e)
    {
        RetryStartupMigrationButton.IsEnabled = false;
        StartupFailureMessage.Text = "正在重新迁移数据库...";
        try
        {
            await App.Services.GetRequiredService<StartupDatabaseInitializer>()
                .InitializeAsync()
                .ConfigureAwait(true);
            StartupFailureBar.IsOpen = false;
            NavFrame.Navigate(typeof(HomePage));
            NavFrame.BackStack.Clear();
        }
        catch (Exception ex)
        {
            ShowStartupFailure(ApplicationFailureClassifier.Classify(ex).DisplayText);
        }
        finally
        {
            RetryStartupMigrationButton.IsEnabled = true;
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        SelectNavigationItem("settings");
        NavFrame.Navigate(typeof(SettingsPage));
        NavFrame.BackStack.Clear();
    }

    private void ShowStartupFailure(string message)
    {
        var initializer = App.Services.GetRequiredService<StartupDatabaseInitializer>();
        StartupFailureMessage.Text = $"{message}\n\n错误日志：{initializer.LogPath}";
        StartupFailureBar.IsOpen = true;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var pageType = item.Tag switch
        {
            "workbench" => typeof(HomePage),
            "collection" => typeof(TasksPage),
            "devices" => typeof(DataPage),
            "audit" => typeof(AuditPage),
            "rules" => typeof(AreasPage),
            "settings" => typeof(SettingsPage),
            "diagnostics" => typeof(DiagnosticsPage),
            _ => throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}")
        };

        if (NavFrame.CurrentSourcePageType != pageType)
        {
            NavFrame.Navigate(pageType);
            NavFrame.BackStack.Clear();
        }
    }

    private void NavigateToData(DataNavigationRequest request)
    {
        SelectNavigationItem("devices");
        NavFrame.Navigate(typeof(DataPage), request);
        NavFrame.BackStack.Clear();
    }

    private void NavigateToAudit()
    {
        SelectNavigationItem("audit");
        NavFrame.Navigate(typeof(AuditPage));
        NavFrame.BackStack.Clear();
    }

    private void NavigateToDates()
    {
        SelectNavigationItem("rules");
        NavFrame.Navigate(typeof(DateManagementPage));
        NavFrame.BackStack.Clear();
    }

    private void SelectNavigationItem(string tag)
    {
        foreach (var item in NavView.MenuItems
                     .Concat(NavView.FooterMenuItems)
                     .OfType<NavigationViewItem>())
        {
            item.IsSelected = string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);
        }
    }
}
