using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class AreasPage : Page
{
    public GroupsViewModel ViewModel { get; }

    public AreasPage()
    {
        ViewModel = App.Services.GetRequiredService<GroupsViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView && listView.SelectedItem is GroupSummaryRow row)
        {
            ViewModel.SelectedGroup = row;
        }
    }

    private void OpenInData_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenSelectedInData();
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDeleteSelectedGroup || ViewModel.SelectedGroup is null)
        {
            return;
        }

        var group = ViewModel.SelectedGroup;
        var result = await ConfirmDeleteAsync(
            "删除自定义分组",
            $"将删除“{group.Name}”及其成员范围和关注规则。\n\n当前 SQLite 设备数据、设备备注和标签不会被删除。");

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteGroupAsync();
        }
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        var item = sender is Button { DataContext: AreaGroupItemRow row }
            ? row
            : ViewModel.SelectedItem;
        if (item is null)
        {
            return;
        }

        var result = await ConfirmDeleteAsync(
            "删除分组成员",
            $"将从当前分组移除：{item.TargetTypeLabel} / {item.TargetLabel}。\n\n这只影响分组筛选范围，不会删除设备数据。");

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteItemAsync(item);
        }
    }

    private async void EditItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AreaGroupItemRow item })
        {
            await ViewModel.BeginEditItemAsync(item);
        }
    }

    private async void DeleteFloor_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDeleteSelectedFloor || ViewModel.SelectedFloorCatalog is null)
        {
            return;
        }

        var floor = ViewModel.SelectedFloorCatalog;
        var result = await ConfirmDeleteAsync(
            "停用楼层目录",
            $"将停用楼层目录：{floor.DisplayLabel}。\n\n已有分组成员不会被删除，但后续下拉选择不再显示该楼层。");

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteFloorAsync();
        }
    }

    private async void DeleteWatch_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDeleteSelectedWatch)
        {
            return;
        }

        var result = await ConfirmDeleteAsync(
            "删除关注规则",
            "将删除当前分组的关注时间窗和异常判定规则。\n\n已经采集的设备数据和分组成员不会被删除。");

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteWatchAsync();
        }
    }

    private void OpenWatchIncident_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenSelectedWatchIncident();
    }

    private async Task<ContentDialogResult> ConfirmDeleteAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync();
    }
}
