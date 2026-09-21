using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using EmsScout.Desktop.Services;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class HomePage : Page
{
    private readonly CancellationTokenSource _pageLifetime = new();

    public HomeViewModel ViewModel { get; }

    public HomePage()
    {
        ViewModel = App.Services.GetRequiredService<HomeViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Yield();
            await ViewModel.LoadAsync(_pageLifetime.Token);
        }
        catch (OperationCanceledException) when (_pageLifetime.IsCancellationRequested)
        {
        }
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

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.RefreshLatestAsync);
    }

    private async void LatestBatch_Click(object sender, RoutedEventArgs e)
    {
        await RunPageOperationAsync(ViewModel.UseLatestDataSourceAsync);
    }

    private async void DataSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is DataSourceOption option)
        {
            await RunPageOperationAsync(token => ViewModel.SelectDataSourceAsync(option, token));
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

    private void AreaGroups_ItemClick(object sender, ItemClickEventArgs e)
    {
        ViewModel.OpenAreaGroup(e.ClickedItem as DashboardAreaGroupRow);
    }

    private void AreaGroupMetric_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is DataNavigationRequest request)
        {
            ViewModel.OpenAreaGroupMetric(request);
        }
    }

    private void OpenAreaGroups_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenAreaGroups();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyMetricCardWidths();

        var compactGroups = e.NewSize.Width < 1250;
        WideAreaGroupList.Visibility = compactGroups ? Visibility.Collapsed : Visibility.Visible;
        CompactAreaGroupList.Visibility = compactGroups ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MetricsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyMetricCardWidths();
    }

    private void MetricsGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        ApplyMetricCardWidths();
    }

    private void ApplyMetricCardWidths()
    {
        var count = ViewModel.Metrics.Count;
        if (count == 0 || MetricsGrid.ActualWidth <= 0)
        {
            return;
        }

        var width = Math.Max(1, Math.Floor((MetricsGrid.ActualWidth - (8 * count)) / count));
        for (var index = 0; index < count; index++)
        {
            ApplyMetricCardWidth(MetricsGrid.ContainerFromIndex(index), width);
        }
    }

    private void ApplyMetricCardWidth(DependencyObject? container, double? width = null)
    {
        if (container is GridViewItem item)
        {
            var cardWidth = width ?? Math.Max(1, Math.Floor((MetricsGrid.ActualWidth - (8 * ViewModel.Metrics.Count)) / Math.Max(1, ViewModel.Metrics.Count)));
            item.Width = cardWidth;
        }
    }
}
