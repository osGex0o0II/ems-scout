using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using EmsScout.Application.Groups;
using EmsScout.Desktop.Services;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class AuditPage : Page
{
    private long? _areaGroupId;

    public AuditViewModel ViewModel { get; }

    public AuditPage()
    {
        ViewModel = App.Services.GetRequiredService<AuditViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync(_areaGroupId, default);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _areaGroupId = (e.Parameter as AuditNavigationRequest)?.AreaGroupId;
    }

    private async void AcceptGroupAdd_Click(object sender, RoutedEventArgs e)
    {
        await DecideGroupChangeAsync(sender, AreaGroupChangeDecision.Accept, "确认加入", "将该设备加入正式成员？");
    }

    private async void RejectGroupAdd_Click(object sender, RoutedEventArgs e)
    {
        await DecideGroupChangeAsync(sender, AreaGroupChangeDecision.Reject, "拒绝并屏蔽", "拒绝本次加入，并把设备加入长期屏蔽名单？");
    }

    private async void AcceptGroupRemove_Click(object sender, RoutedEventArgs e)
    {
        await DecideGroupChangeAsync(sender, AreaGroupChangeDecision.Accept, "确认移除", "将该设备从正式成员中移除？");
    }

    private async void RejectGroupRemove_Click(object sender, RoutedEventArgs e)
    {
        await DecideGroupChangeAsync(sender, AreaGroupChangeDecision.Reject, "拒绝并保留", "拒绝本次移除，并把设备设为手动保留？");
    }

    private async Task DecideGroupChangeAsync(
        object sender,
        AreaGroupChangeDecision decision,
        string actionLabel,
        string prompt)
    {
        if (sender is not Button { DataContext: AreaGroupChangeRow row })
        {
            return;
        }

        if (!await ConfirmAsync(actionLabel, $"{row.DeviceLabel}\n\n{prompt}\n备注：{row.DecisionNote}", actionLabel))
        {
            return;
        }

        await ViewModel.DecideChangeAsync(row, decision, row.DecisionNote);
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenData();
    }

    private void DataContext_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is DataContextOption option)
        {
            ViewModel.DataContext.Select(option);
        }
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
