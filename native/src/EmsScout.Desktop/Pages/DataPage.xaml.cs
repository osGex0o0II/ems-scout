using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using EmsScout.Desktop.Services;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class DataPage : Page
{
    private DataNavigationRequest? _navigationRequest;

    public DataViewModel ViewModel { get; }

    public DataPage()
    {
        ViewModel = App.Services.GetRequiredService<DataViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync(_navigationRequest);
        _navigationRequest = null;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _navigationRequest = e.Parameter as DataNavigationRequest;
        base.OnNavigatedTo(e);
    }

    private async void ApplyFilters_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ApplyFiltersAsync();
    }

    private async void BuildingFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ViewModel.ApplyBuildingSelectionAsync();
    }

    private async void FloorFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await ViewModel.ApplyFloorSelectionAsync();
    }

    private async void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResetFiltersAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.MovePreviousAsync();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.MoveNextAsync();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ExportAsync();
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
