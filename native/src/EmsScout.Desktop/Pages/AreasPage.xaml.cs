using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class AreasPage : Page
{
    private long? _requestedGroupId;

    public GroupsViewModel ViewModel { get; }

    public AreasPage()
    {
        ViewModel = App.Services.GetRequiredService<GroupsViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        if (_requestedGroupId is not null)
        {
            ViewModel.SelectGroup(_requestedGroupId.Value);
            _requestedGroupId = null;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _requestedGroupId = e.Parameter is long groupId ? groupId : null;
        base.OnNavigatedTo(e);
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView && listView.SelectedItem is GroupSummaryRow row)
        {
            ViewModel.SelectedGroup = row;
        }
    }

    private async void MemberScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.IsMemberDraftActive)
        {
            await ViewModel.RefreshTargetOptionsAsync();
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
            "删除区域组",
            $"将删除“{group.Name}”以及已添加的楼层和设备。\n\n当前设备数据、设备备注和标签不会被删除。");

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
            "移除已添加内容",
            $"将从当前区域组移除：{item.TargetTypeLabel} / {item.TargetLabel}。\n\n这只影响区域组筛选，不会删除设备数据。");

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
            $"将停用可选楼层：{floor.DisplayLabel}。\n\n区域组里已经添加的内容不会被删除，但后续下拉选择不再显示该楼层。");

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteFloorAsync();
        }
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
