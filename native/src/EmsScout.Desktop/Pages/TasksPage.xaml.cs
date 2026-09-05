using System.Collections.Specialized;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using EmsScout.Desktop.ViewModels;

namespace EmsScout.Desktop.Pages;

public sealed partial class TasksPage : Page
{
    public CollectionTaskViewModel ViewModel { get; }
    private bool _loaded;
    private bool _logsSubscribed;

    public TasksPage()
    {
        ViewModel = App.Services.GetRequiredService<CollectionTaskViewModel>();
        InitializeComponent();
        AttachLogs();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        AttachLogs();
        if (!_loaded)
        {
            await ViewModel.InitializeAsync();
            if (ViewModel.CheckEnvironmentCommand.CanExecute(null))
            {
                await ViewModel.CheckEnvironmentCommand.ExecuteAsync(null);
            }
            _loaded = true;
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachLogs();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 980;
        if (narrow)
        {
            SetupColumn.Width = new GridLength(1, GridUnitType.Star);
            ExecutionColumn.Width = new GridLength(0);
            SetupRow.Height = new GridLength(0.48, GridUnitType.Star);
            ExecutionRow.Height = new GridLength(0.52, GridUnitType.Star);
            Grid.SetColumn(SetupPanel, 0);
            Grid.SetRow(SetupPanel, 0);
            Grid.SetColumn(ExecutionPanel, 0);
            Grid.SetRow(ExecutionPanel, 1);
            WorkflowGrid.ColumnSpacing = 0;
            WorkflowGrid.RowSpacing = 12;
            return;
        }

        SetupColumn.Width = new GridLength(0.9, GridUnitType.Star);
        ExecutionColumn.Width = new GridLength(1.35, GridUnitType.Star);
        SetupRow.Height = new GridLength(1, GridUnitType.Star);
        ExecutionRow.Height = new GridLength(0);
        Grid.SetColumn(SetupPanel, 0);
        Grid.SetRow(SetupPanel, 0);
        Grid.SetColumn(ExecutionPanel, 1);
        Grid.SetRow(ExecutionPanel, 0);
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
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "开始采集",
            Content = $"范围：{string.Join("、", buildings)}\n\n{ViewModel.CurrentDataImpactText}\n采集期间请保持采集浏览器和 EMS 页面开启。",
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
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

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not NotifyCollectionChangedAction.Add || ViewModel.FilteredLogs.Count == 0)
        {
            return;
        }

        LogsList.ScrollIntoView(ViewModel.FilteredLogs[^1]);
    }

    private void AttachLogs()
    {
        if (_logsSubscribed)
        {
            return;
        }

        ViewModel.FilteredLogs.CollectionChanged += Logs_CollectionChanged;
        _logsSubscribed = true;
    }

    private void DetachLogs()
    {
        if (!_logsSubscribed)
        {
            return;
        }

        ViewModel.FilteredLogs.CollectionChanged -= Logs_CollectionChanged;
        _logsSubscribed = false;
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.FilteredLogs.Count == 0)
        {
            return;
        }

        var text = string.Join(
            Environment.NewLine,
            ViewModel.FilteredLogs.Select(log => $"[{log.Time}] [{log.Severity}] {log.Message}"));
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearLogs();
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

public sealed class LogSeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value?.ToString() switch
        {
            "ERROR" => "SystemFillColorCriticalBrush",
            "WARN" => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
        };

        return Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class LogSeverityGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value?.ToString() switch
    {
        "ERROR" => "\uE783",
        "WARN" => "\uE7BA",
        _ => "\uE946",
    };

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
