using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EmsScout.Desktop.Services;
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

    private void DataContext_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is DataContextOption option)
        {
            ViewModel.DataContext.Select(option);
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

    private void LocateAttention_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DashboardRiskRow row })
        {
            ViewModel.OpenRisk(row);
        }
    }

    private async void AcknowledgeAttention_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DashboardRiskRow row })
        {
            await ViewModel.AcknowledgeAttentionAsync(row);
        }
    }

    private async void IgnoreAttention_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DashboardRiskRow row })
        {
            return;
        }

        var reasonBox = new TextBox
        {
            Header = "忽略原因",
            PlaceholderText = "说明为什么当前无需处理",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 360,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "忽略待处理事项",
            Content = reasonBox,
            PrimaryButtonText = "忽略",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };
        reasonBox.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(reasonBox.Text);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            !string.IsNullOrWhiteSpace(reasonBox.Text))
        {
            await ViewModel.IgnoreAttentionAsync(row, reasonBox.Text);
        }
    }

    private async void ReopenAttention_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DashboardRiskRow row })
        {
            await ViewModel.ReopenAttentionAsync(row);
        }
    }
}
