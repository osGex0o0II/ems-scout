using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using EmsScout.Desktop.Pages;
using EmsScout.Desktop.Services;

namespace EmsScout.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        AppWindow.SetIcon("Assets/AppIcon.ico");
        App.Services.GetRequiredService<WindowHandleProvider>().Attach(this);
        App.Services.GetRequiredService<AppUiSettingsService>().ApplyTheme(RootGrid);
        App.Services.GetRequiredService<NavigationService>().Attach(NavigateToData);
        NavFrame.Navigate(typeof(HomePage));
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var pageType = item.Tag switch
        {
            "overview" => typeof(HomePage),
            "tasks" => typeof(TasksPage),
            "data" => typeof(DataPage),
            "audit" => typeof(AuditPage),
            "groups" => typeof(AreasPage),
            "settings" => typeof(SettingsPage),
            "diagnostics" => typeof(DiagnosticsPage),
            _ => throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}")
        };

        if (NavFrame.CurrentSourcePageType != pageType)
        {
            NavFrame.Navigate(pageType);
        }
    }

    private void NavigateToData(DataNavigationRequest request)
    {
        SelectNavigationItem("data");
        NavFrame.Navigate(typeof(DataPage), request);
    }

    private void SelectNavigationItem(string tag)
    {
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            item.IsSelected = string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);
        }
    }
}
