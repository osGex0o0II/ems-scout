using EmsScout.Desktop.Services;
using EmsScout.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace EmsScout.Desktop.Pages;

public sealed partial class AreasPage : Page
{
    private readonly WindowHandleProvider _windowHandleProvider;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private bool _suppressGroupSelectionChanged;
    private long? _requestedGroupId;

    public GroupsViewModel ViewModel { get; }

    public AreasPage()
    {
        ViewModel = App.Services.GetRequiredService<GroupsViewModel>();
        _windowHandleProvider = App.Services.GetRequiredService<WindowHandleProvider>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadAsync();
            if (_requestedGroupId is long groupId)
            {
                await ViewModel.SelectGroupAsync(groupId);
                _requestedGroupId = null;
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("区域组页面加载失败", ex.Message);
        }
    }

    private async void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGroupSelectionChanged || e.AddedItems.Count == 0 || e.AddedItems[0] is not GroupSummaryRow requestedGroup)
            return;

        if (ReferenceEquals(ViewModel.SelectedGroup, requestedGroup))
            return;

        var currentGroup = ViewModel.SelectedGroup;
        if (ViewModel.HasUnsavedChanges)
        {
            _suppressGroupSelectionChanged = true;
            if (sender is ListView groupList)
                groupList.SelectedItem = currentGroup;
            _suppressGroupSelectionChanged = false;

            var result = await ConfirmUnsavedGroupSwitchAsync();
            if (result == ContentDialogResult.None)
                return;

            if (result == ContentDialogResult.Primary && !await ViewModel.SaveGroupAsync())
                return;

            if (result == ContentDialogResult.Secondary)
                ViewModel.DiscardChanges();
        }

        await ViewModel.SelectGroupAsync(requestedGroup.Id);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _requestedGroupId = e.Parameter is long groupId ? groupId : null;
        base.OnNavigatedTo(e);
    }

    private async void RuleRowBuilding_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (sender is ComboBox { DataContext: AreaGroupRuleRow row })
                await ViewModel.RefreshRuleOptionsAsync(row);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("加载楼层失败", ex.Message);
        }
    }

    private async void DeleteRuleRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AreaGroupRuleRow row })
            return;

        if (!ViewModel.CanEditSelectedGroup)
            return;

        await ViewModel.DeleteRuleRowAsync(row);
    }

    private void OpenInData_Click(object sender, RoutedEventArgs e) => ViewModel.OpenSelectedInData();

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGroup is not { } group)
            return;

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = $"区域组：{group.Name}" });
        content.Children.Add(new TextBlock { Text = $"规则数量：{group.ItemCount:N0} 条" });
        content.Children.Add(new TextBlock { Text = $"覆盖设备：{group.Count:N0} 台" });
        content.Children.Add(new TextBlock { Text = $"覆盖区域：{group.CoveredAreas:N0} 个" });
        content.Children.Add(new TextBlock
        {
            Text = "删除后，区域组及其匹配规则将从本地数据库移除，无法恢复。",
            Foreground = GetThemeBrush("SystemFillColorCriticalBrush", Color.FromArgb(255, 196, 43, 28)),
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        if (ViewModel.HasUnsavedChanges)
        {
            content.Children.Add(new TextBlock
            {
                Text = "当前存在未保存修改，删除时将一并放弃。",
                Foreground = GetThemeBrush("SystemFillColorCautionBrush", Color.FromArgb(255, 196, 119, 6)),
                TextWrapping = TextWrapping.WrapWholeWords,
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除区域组",
            Content = content,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await ViewModel.DeleteGroupCommand.ExecuteAsync(null);
    }

    private async void ExportRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"区域组规则_{DateTime.Now:yyyyMMdd_HHmmss}",
            };
            picker.FileTypeChoices.Add("JSON 文件", [".json"]);
            InitializeWithWindow.Initialize(picker, _windowHandleProvider.GetWindowHandle());
            var file = await picker.PickSaveFileAsync();
            if (file is not null) await ViewModel.ExportRulesAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("导出规则失败", ex.Message);
        }
    }

    private async void ImportRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            InitializeWithWindow.Initialize(picker, _windowHandleProvider.GetWindowHandle());
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await ViewModel.ImportRulesAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("导入规则失败", ex.Message);
        }
    }

    private async Task ShowErrorAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowDialogAsync(dialog);
    }

    private async Task<ContentDialogResult> ConfirmUnsavedGroupSwitchAsync()
    {
        var group = ViewModel.SelectedGroup;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = $"当前区域组：{group?.Name ?? ViewModel.EditName}" });
        content.Children.Add(new TextBlock { Text = $"当前规则：{ViewModel.Rules.Count:N0} 条" });
        content.Children.Add(new TextBlock { Text = $"已保存覆盖设备：{(group?.Count ?? 0):N0} 台" });
        content.Children.Add(new TextBlock { Text = $"已保存覆盖区域：{(group?.CoveredAreas ?? 0):N0} 个" });
        content.Children.Add(new TextBlock
        {
            Text = "当前规则尚未保存。请选择保存修改、放弃修改，或取消切换。",
            Foreground = GetThemeBrush("SystemFillColorCautionBrush", Color.FromArgb(255, 196, 119, 6)),
            TextWrapping = TextWrapping.WrapWholeWords,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "切换区域组前处理未保存修改",
            Content = content,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "放弃",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowDialogAsync(dialog);
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private static Brush GetThemeBrush(string key, Color fallback)
    {
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush)
            return brush;

        return new SolidColorBrush(fallback);
    }
}
