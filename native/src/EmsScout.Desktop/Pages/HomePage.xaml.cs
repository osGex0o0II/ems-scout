using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; }

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await Task.Yield();
        await ViewModel.LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshLatestAsync();
    }

    private async void LatestBatch_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.UseLatestDataSourceAsync();
    }

    private async void DataSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is DataSourceOption option)
        {
            await ViewModel.SelectDataSourceAsync(option);
        }
    }

    private void Metrics_ItemClick(object sender, ItemClickEventArgs e)
    {
        ViewModel.OpenMetric(e.ClickedItem as MetricItem);
    }

    private void Buildings_ItemClick(object sender, ItemClickEventArgs e)
    {
        ViewModel.OpenBuilding(e.ClickedItem as BuildingSummaryRow);
    }

    private void Risks_ItemClick(object sender, ItemClickEventArgs e)
    {
        ViewModel.OpenRisk(e.ClickedItem as DashboardRiskRow);
    }

    private void AreaGroups_ItemClick(object sender, ItemClickEventArgs e)
    {
        ViewModel.OpenAreaGroup(e.ClickedItem as DashboardAreaGroupRow);
    }

    private void OpenAreaGroups_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenAreaGroups();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compactGroups = e.NewSize.Width < 1050;
        WideAreaGroupList.Visibility = compactGroups ? Visibility.Collapsed : Visibility.Visible;
        CompactAreaGroupList.Visibility = compactGroups ? Visibility.Visible : Visibility.Collapsed;

        var splitWorkspace = e.NewSize.Width >= 1600;
        SecondaryWorkspaceColumn.Width = splitWorkspace
            ? new GridLength(0.78, GridUnitType.Star)
            : new GridLength(0);
        Grid.SetColumn(RiskPanel, splitWorkspace ? 1 : 0);
        Grid.SetRow(RiskPanel, splitWorkspace ? 0 : 1);
        PrimaryWorkspace.ColumnSpacing = splitWorkspace ? 14 : 0;
        PrimaryWorkspace.RowSpacing = splitWorkspace ? 0 : 14;
    }
}
