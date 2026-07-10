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
    }

    private void ViewModel_SettingsApplied(object? sender, EventArgs e)
    {
        if (XamlRoot?.Content is FrameworkElement root)
        {
            App.Services.GetRequiredService<AppUiSettingsService>().ApplyTheme(root);
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

    private async void PickExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            ViewModel.ExportDirectory = path;
        }
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
