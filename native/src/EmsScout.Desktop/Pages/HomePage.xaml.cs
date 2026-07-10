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
        await ViewModel.LoadAsync();
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
}
