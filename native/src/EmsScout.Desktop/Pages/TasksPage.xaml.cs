using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EmsScout.Application.Collection;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class TasksPage : Page
{
    public CollectionTaskViewModel ViewModel { get; }
    private bool _loaded;

    public TasksPage()
    {
        ViewModel = App.Services.GetRequiredService<CollectionTaskViewModel>();
        InitializeComponent();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            try
            {
                await ViewModel.InitializeAsync();
                if (ViewModel.CheckEnvironmentCommand.CanExecute(null))
                {
                    await ViewModel.CheckEnvironmentCommand.ExecuteAsync(null);
                }
            }
            catch (Exception ex)
            {
                ViewModel.ReportInitializationError(ex);
            }
            _loaded = true;
        }

        ViewModel.StartEnvironmentMonitoring();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.StopEnvironmentMonitoring();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 980;
        if (narrow)
        {
            WorkflowScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            SetupPanel.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            SetupColumn.Width = new GridLength(1, GridUnitType.Star);
            ExecutionColumn.Width = new GridLength(0);
            SetupRow.Height = new GridLength(1, GridUnitType.Auto);
            ExecutionRow.Height = new GridLength(1, GridUnitType.Auto);
            Grid.SetColumn(SetupPanel, 0);
            Grid.SetRow(SetupPanel, 0);
            Grid.SetColumn(ExecutionPanel, 0);
            Grid.SetRow(ExecutionPanel, 1);
            WorkflowGrid.VerticalAlignment = VerticalAlignment.Top;
            WorkflowGrid.ColumnSpacing = 0;
            WorkflowGrid.RowSpacing = 12;
            return;
        }

        WorkflowScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetupPanel.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        SetupColumn.Width = new GridLength(0.9, GridUnitType.Star);
        ExecutionColumn.Width = new GridLength(1.35, GridUnitType.Star);
        SetupRow.Height = new GridLength(1, GridUnitType.Star);
        ExecutionRow.Height = new GridLength(0);
        Grid.SetColumn(SetupPanel, 0);
        Grid.SetRow(SetupPanel, 0);
        Grid.SetColumn(ExecutionPanel, 1);
        Grid.SetRow(ExecutionPanel, 0);
        WorkflowGrid.VerticalAlignment = VerticalAlignment.Stretch;
        WorkflowGrid.ColumnSpacing = 14;
        WorkflowGrid.RowSpacing = 0;
    }

    private async void StartTask_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanStartTask || !ViewModel.StartCommand.CanExecute(null))
        {
            return;
        }

        var buildings = ViewModel.Buildings
            .Where(building => building.IsSelected)
            .Select(building => building.Value)
            .ToList();
        var strategyButtons = new RadioButtons
        {
            Header = "采集模式",
        };
        strategyButtons.Items.Add("稳定模式");
        strategyButtons.Items.Add("快速模式");
        strategyButtons.SelectedIndex = 0;

        var dialogContent = new StackPanel { Spacing = 12 };
        dialogContent.Children.Add(new TextBlock
        {
            Text = $"范围：{string.Join("、", buildings)}",
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        dialogContent.Children.Add(strategyButtons);
        dialogContent.Children.Add(new TextBlock
        {
            Text = "采集期间请保持采集浏览器和 EMS 页面开启。",
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "开始采集",
            Content = dialogContent,
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.SetNextRealtimeCollectionStrategy(
                CollectionTaskModeValues.RealtimeStrategyForSelection(strategyButtons.SelectedIndex));
            await ViewModel.StartCommand.ExecuteAsync(null);
        }
    }

    private async void StopTask_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.StopCommand.CanExecute(null))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "停止当前任务",
            Content = "采集进程将被终止。已经完成的数据库更新不会自动回滚；停止后页面会明确说明当前数据是否已经更新。",
            PrimaryButtonText = "停止任务",
            CloseButtonText = "继续运行",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.StopCommand.Execute(null);
        }
    }

    private async void DeleteRun_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDeleteSelectedRun || ViewModel.SelectedRun is null)
        {
            return;
        }

        var run = ViewModel.SelectedRun;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除历史批次",
            Content = $"将删除批次 #{run.Id} 的历史快照和证据记录。\n\n当前 SQLite 数据、设备备注和标签不会被删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteRunAsync();
        }
    }

    private void PreflightDetails_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PreflightExpanded = !ViewModel.PreflightExpanded;
    }

    public static bool IsNotNullOrEmpty(string? value) => !string.IsNullOrWhiteSpace(value);

    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InverseBoolToVisibility(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility NotNullOrWhiteSpaceToVisibility(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
}
