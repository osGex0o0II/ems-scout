using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using EmsScout.Application;
using EmsScout.Application.Settings;
using EmsScout.Desktop.Pages;
using EmsScout.Desktop.Services;
using WinUIEx;
using Windows.Graphics;

namespace EmsScout.Desktop;

public sealed partial class MainWindow : Window
{
    private bool _suppressNavigationSelection;
    private bool _isNavigating;
    private bool _exitRequested;
    private readonly WindowManager _windowManager;
    private readonly AppSettingsService _settingsService;
    private readonly AppUiSettingsService _uiSettings;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _settingsService = App.Services.GetRequiredService<AppSettingsService>();
        _uiSettings = App.Services.GetRequiredService<AppUiSettingsService>();
        _windowManager = WindowManager.Get(this);
        _windowManager.IsVisibleInTray = _uiSettings.CloseToTray;
        _windowManager.TrayIconContextMenu += WindowManager_TrayIconContextMenu;
        _uiSettings.SettingsChanged += UiSettings_SettingsChanged;
        AppWindow.Closing += AppWindow_Closing;
        WindowSizeConstraint.Attach(this);
        WindowSizeConstraint.Restore(this);
        AppWindow.Resize(WindowSizeConstraint.ScaleSizeForWindow(this, new SizeInt32(
            WindowSizeConstraint.InitialClientWidth,
            WindowSizeConstraint.InitialClientHeight)));
        var settings = _settingsService.Current;
        if (settings.SaveWindowPlacement && settings.WindowPlacement is not null)
        {
            WindowSizeConstraint.Restore(this, settings.WindowPlacement);
        }
        App.Services.GetRequiredService<WindowHandleProvider>().Attach(this);
        App.Services.GetRequiredService<AppUiSettingsService>().ApplyTheme(RootGrid);
        App.Services.GetRequiredService<NavigationService>().Attach(NavigateToData, NavigateToGroups);
        NavigateToPage(typeof(HomePage));
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowPlacement();
        if (!TrayClosePolicy.ShouldHideOnClose(_windowManager.IsVisibleInTray, _exitRequested))
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
        _windowManager.AppWindow.IsShownInSwitchers = false;
    }

    private void WindowManager_TrayIconContextMenu(WindowManager sender, TrayIconEventArgs args)
    {
        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "打开窗口" };
        openItem.Click += (_, _) => ShowFromTray();
        flyout.Items.Add(openItem);

        var latestItem = new MenuFlyoutItem { Text = "查看最新状态" };
        latestItem.Click += (_, _) =>
        {
            SelectNavigationItem("overview");
            NavigateToPage(typeof(HomePage));
            ShowFromTray();
        };
        flyout.Items.Add(latestItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "退出程序" };
        exitItem.Click += (_, _) => ExitFromTray();
        flyout.Items.Add(exitItem);

        args.Flyout = flyout;
    }

    private void ShowFromTray()
    {
        _windowManager.AppWindow.IsShownInSwitchers = true;
        Activate();
    }

    public void ShowFromActivation()
    {
        ShowFromTray();
    }

    public void ApplyStartupState()
    {
        if (_settingsService.Current.StartMinimized)
        {
            WindowSizeConstraint.Minimize(this);
        }
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        _windowManager.IsVisibleInTray = false;
        Close();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigationSelection)
        {
            return;
        }

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

        NavigateToPage(pageType);
    }

    private void NavigateToData(DataNavigationRequest request)
    {
        SelectNavigationItem("data");
        NavigateToPage(typeof(DataPage), request);
    }

    private void NavigateToGroups(long? groupId)
    {
        SelectNavigationItem("groups");
        NavigateToPage(typeof(AreasPage), groupId);
    }

    private void SelectNavigationItem(string tag)
    {
        _suppressNavigationSelection = true;
        try
        {
            foreach (var item in NavView.MenuItems
                         .Concat(NavView.FooterMenuItems)
                         .OfType<NavigationViewItem>())
            {
                item.IsSelected = string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _suppressNavigationSelection = false;
        }
    }

    private void NavigateToPage(Type pageType, object? parameter = null)
    {
        if (_isNavigating || (parameter is null && NavFrame.CurrentSourcePageType == pageType))
        {
            return;
        }

        _isNavigating = true;
        try
        {
            NavFrame.Navigate(pageType, parameter, CreatePageTransition());
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private void UiSettings_SettingsChanged(object? sender, EventArgs e)
    {
        _windowManager.IsVisibleInTray = _uiSettings.CloseToTray;
    }

    private NavigationTransitionInfo CreatePageTransition()
    {
        if (_uiSettings.ReduceMotion ||
            string.Equals(_uiSettings.PageTransitionStyle, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new SuppressNavigationTransitionInfo();
        }

        if (string.Equals(_uiSettings.PageTransitionStyle, "slide", StringComparison.OrdinalIgnoreCase))
        {
            return new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight };
        }

        return new EntranceNavigationTransitionInfo();
    }

    private void SaveWindowPlacement()
    {
        var settings = _settingsService.Current;
        if (!settings.SaveWindowPlacement)
        {
            return;
        }

        settings.WindowPlacement = WindowSizeConstraint.Capture(this);
        _settingsService.Save(settings);
    }
}
