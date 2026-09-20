using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EmsScout.Desktop.Services;
using EmsScout.Desktop.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EmsScout.Desktop.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly WindowHandleProvider _windowHandleProvider;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        _windowHandleProvider = App.Services.GetRequiredService<WindowHandleProvider>();
        ViewModel.SettingsApplied += ViewModel_SettingsApplied;
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Load();
        ShowSection("connection");
    }

    private void SectionNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            ShowSection(item.Tag?.ToString() ?? "connection");
        }
    }

    private void ShowSection(string section)
    {
        ConnectionSection.Visibility = section == "connection" ? Visibility.Visible : Visibility.Collapsed;
        DirectoriesSection.Visibility = section == "directories" ? Visibility.Visible : Visibility.Collapsed;
        CollectionSection.Visibility = section == "collection" ? Visibility.Visible : Visibility.Collapsed;
        DataTableSection.Visibility = section == "data-table" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = section == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        BehaviorSection.Visibility = section == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedSection.Visibility = section == "advanced" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ViewModel_SettingsApplied(object? sender, EventArgs e)
    {
        if (XamlRoot?.Content is FrameworkElement root)
        {
            App.Services.GetRequiredService<AppUiSettingsService>().ApplyCurrentSettings(root);
        }

        _ = ApplySystemIntegrationsAsync();
    }

    private async Task ApplySystemIntegrationsAsync()
    {
        try
        {
            var settings = App.Services.GetRequiredService<EmsScout.Application.Settings.AppSettingsService>().Current;
            var startupApplied = await App.Services.GetRequiredService<StartupTaskService>().ApplyAsync(settings.LaunchAtLogin);
            if (settings.LaunchAtLogin && !startupApplied)
            {
                ViewModel.SetStatus("设置已保存；当前运行环境未启用登录后自动启动。");
            }

            App.Services.GetRequiredService<SendToShortcutService>().Apply(settings.ShowInSendTo);
        }
        catch
        {
            // Optional shell integration must not block settings or navigation.
            ViewModel.SetStatus("设置已保存；系统集成选项未能应用，请检查当前 Windows 环境。");
        }
    }

    private async void PickDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            ViewModel.DataDirectory = path;
        }
    }

    private async void ClearLocalLogs_Click(object sender, RoutedEventArgs e)
    {
        var preview = ViewModel.PreviewLocalLogCleanup();
        var content = preview.FileCount == 0
            ? "当前数据目录没有可清理的本地日志。"
            : $"将清理 {preview.FileCount:N0} 个本地日志文件，共 {preview.TotalBytes:N0} 字节。\n\n" +
              "不会删除 SQLite、JSON、NDJSON、质量报告、实时报告或导出文件。";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空本地日志？",
            Content = content,
            PrimaryButtonText = preview.FileCount == 0 ? "关闭" : "清空日志",
            CloseButtonText = preview.FileCount == 0 ? null : "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (preview.FileCount == 0 || await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = ViewModel.ClearLocalLogs();
        var detail = $"已清理：{result.DeletedCount:N0} 个\n跳过：{result.SkippedPaths.Count:N0} 个\n失败：{result.FailedPaths.Count:N0} 个";
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = result.IsComplete ? "本地日志已清理" : "本地日志清理未完全成功",
            Content = detail,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _windowHandleProvider.GetWindowHandle());
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
