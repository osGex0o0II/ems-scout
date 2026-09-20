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

    private async void DeleteRun_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CollectionRunRow row })
        {
            ViewModel.SelectedRun = row;
        }

        if (!ViewModel.CanDeleteSelectedRun || ViewModel.SelectedRun is null)
        {
            return;
        }

        var run = ViewModel.SelectedRun;
        var impact = await ViewModel.GetSelectedDeleteImpactAsync();
        var artifactCount = impact?.Artifacts.Count(candidate => !candidate.IsShared) ?? 0;
        var content =
            $"完成时间：{run.CompletedAt}\n" +
            $"范围：{run.ScopeLabel}\n" +
            $"卡片数量：{run.CountLabel}\n" +
            $"采集模式：{run.CollectionModeLabel}\n" +
            $"版本：{run.VersionLabel}\n\n" +
            $"将删除该批次的 SQLite 数据、批次 JSON、NDJSON、质量报告和实时报告。\n" +
            $"预计清理本地文件：{artifactCount:N0} 个。\n" +
            "删除后无法恢复，日志文件不会随批次删除。";
        var result = await ConfirmAsync("确认删除该批次？", content, "删除批次");
        if (result)
        {
            await ViewModel.DeleteRunAsync();
        }
    }

    private void IssueCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list && list.SelectedItem is CollectionIssueCategoryRow category)
        {
            ViewModel.ShowIssueDetails(category);
        }
    }

    private void ShowDetails_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowIssueDetails(ViewModel.SelectedIssueCategory);
    }

    private void ShowIssues_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowIssues();
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
