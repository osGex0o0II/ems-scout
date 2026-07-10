using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class AuditPage : Page
{
    public AuditViewModel ViewModel { get; }

    public AuditPage()
    {
        ViewModel = App.Services.GetRequiredService<AuditViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenData();
    }

    private async void RestoreRun_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanRestoreSelectedRun || ViewModel.SelectedRun is null)
        {
            return;
        }

        var run = ViewModel.SelectedRun;
        var scopeText = run.Scope.Equals("partial", StringComparison.OrdinalIgnoreCase)
            ? $"只会替换 {string.Join("、", run.Buildings)} 的当前数据，其他楼栋保持不变。"
            : "将替换全部楼栋的当前数据。";
        var result = await ConfirmAsync(
            "恢复历史批次",
            $"将把批次 #{run.Id} 恢复为当前数据，共 {run.CardCount:N0} 张卡片。\n\n{scopeText}\n恢复前会自动备份当前数据；备注和标签不会被删除。",
            "恢复");
        if (result)
        {
            await ViewModel.RestoreRunAsync();
        }
    }

    private async void DeleteRun_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDeleteSelectedRun || ViewModel.SelectedRun is null)
        {
            return;
        }

        var run = ViewModel.SelectedRun;
        var result = await ConfirmAsync(
            "删除历史批次",
            $"将删除批次 #{run.Id} 的历史快照和证据记录。\n\n当前 SQLite 数据、设备备注和标签不会被删除。",
            "删除");
        if (result)
        {
            await ViewModel.DeleteRunAsync();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
