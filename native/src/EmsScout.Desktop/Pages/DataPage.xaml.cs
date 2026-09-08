using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using EmsScout.Desktop.Services;
using EmsScout.Desktop.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EmsScout.Desktop.Pages;

public sealed partial class DataPage : Page
{
    private DataNavigationRequest? _navigationRequest;
    private readonly WindowHandleProvider _windowHandleProvider;
    private readonly CancellationTokenSource _pageLifetime = new();

    public DataViewModel ViewModel { get; }

    public DataPage()
    {
        ViewModel = App.Services.GetRequiredService<DataViewModel>();
        _windowHandleProvider = App.Services.GetRequiredService<WindowHandleProvider>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeAsync(_navigationRequest, _pageLifetime.Token);
        }
        catch (OperationCanceledException) when (_pageLifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            ViewModel.ReportInitializationError(ex);
        }
        finally
        {
            _navigationRequest = null;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _navigationRequest = e.Parameter as DataNavigationRequest;
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageLifetime.Cancel();
        base.OnNavigatedFrom(e);
    }

    private async Task RunPageOperationAsync(Func<CancellationToken, Task> operation)
    {
        try
        {
            await operation(_pageLifetime.Token);
        }
        catch (OperationCanceledException) when (_pageLifetime.IsCancellationRequested)
        {
        }
    }

    private async void ApplyFilters_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.ApplyFiltersAsync);
    }

    private async void BuildingFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.ApplyBuildingSelectionAsync);
    }

    private async void FloorFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.ApplyFloorSelectionAsync);
    }

    private async void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.ResetFiltersAsync);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.RefreshAsync);
    }

    private async void DataSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is DataSourceOption option)
        {
            await RunPageOperationAsync(token => ViewModel.SelectDataSourceAsync(option, token));
        }
    }

    private async void LatestBatch_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.UseLatestDataSourceAsync);
    }

    private async void RefreshLatest_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.RefreshLatestAsync);
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.MovePreviousAsync);
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.MoveNextAsync);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"数据管理筛选结果_{DateTime.Now:yyyyMMdd_HHmmss}",
        };
        picker.FileTypeChoices.Add("Excel 工作簿", [".xlsx"]);
        InitializeWithWindow.Initialize(picker, _windowHandleProvider.GetWindowHandle());
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            ViewModel.ReportExportCanceled();
            return;
        }

        await RunPageOperationAsync(token => ViewModel.ExportAsync(file.Path, token));
    }

    private void OpenExportLocation_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenLastExportLocation();
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView && listView.SelectedItem is DataDeviceRow row)
        {
            ViewModel.SelectedDevice = row;
        }
    }
}
