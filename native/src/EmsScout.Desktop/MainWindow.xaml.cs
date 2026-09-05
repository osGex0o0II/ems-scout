using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using EmsScout.Desktop.Pages;
using EmsScout.Desktop.Services;
using Windows.Graphics;

namespace EmsScout.Desktop;

public sealed partial class MainWindow : Window
{
    private bool _initialSizeApplied;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        WindowSizeConstraint.Attach(this);
        Activated += MainWindow_Activated;
        App.Services.GetRequiredService<WindowHandleProvider>().Attach(this);
        App.Services.GetRequiredService<AppUiSettingsService>().ApplyTheme(RootGrid);
        App.Services.GetRequiredService<NavigationService>().Attach(NavigateToData, NavigateToGroups);
        NavFrame.Navigate(typeof(HomePage));
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialSizeApplied)
        {
            return;
        }

        _initialSizeApplied = true;
        Activated -= MainWindow_Activated;
        WindowSizeConstraint.Restore(this);
        AppWindow.Resize(WindowSizeConstraint.ScaleSizeForWindow(this, new SizeInt32(1280, 820)));
        WindowSizeConstraint.Restore(this);
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

    private void NavigateToGroups(long? groupId)
    {
        SelectNavigationItem("groups");
        NavFrame.Navigate(typeof(AreasPage), groupId);
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
