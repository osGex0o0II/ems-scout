using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmsScout.Application.Collection;

namespace EmsScout.Desktop.Services;

public sealed partial class DataContextService(ICollectionRunRepository collectionRunRepository) : ObservableObject
{
    public ObservableCollection<DataContextOption> Options { get; } = [];

    [ObservableProperty]
    public partial DataContextOption? Selected { get; set; }

    public long? RunId => Selected?.RunId;

    public string DisplayText => Selected?.DisplayText ?? "当前数据";

    public bool IsHistory => Selected?.RunId is not null;

    public bool IsReadOnly => Selected?.IsReadOnly ?? false;

    public event EventHandler? ContextChanged;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var previousRunId = RunId;
        var runs = await collectionRunRepository.ListAsync(80, cancellationToken).ConfigureAwait(true);
        Options.Clear();
        var latestCompletedAt = runs
            .Where(run => run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(run => run.CompletedAt)
            .Select(run => run.CompletedAt)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var currentLabel = latestCompletedAt is null
            ? "未记录时间"
            : FormatDate(latestCompletedAt);
        Options.Add(new DataContextOption(
            null,
            currentLabel,
            latestCompletedAt is null
                ? "当前库 · 可编辑"
                : $"最近更新 {FormatDate(latestCompletedAt)} · 可编辑",
            false));
        foreach (var run in runs.Where(run => run.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)))
        {
            Options.Add(new DataContextOption(
                run.Id,
                $"{FormatDate(run.CompletedAt)} · {run.ScopeLabel}",
                $"批次 #{run.Id} · {run.CountLabel} · 只读",
                true));
        }

        Selected = Options.FirstOrDefault(option => option.RunId == previousRunId) ?? Options[0];
    }

    public void Select(DataContextOption? option)
    {
        if (option is null || option.RunId == RunId)
        {
            return;
        }

        Selected = option;
    }

    partial void OnSelectedChanged(DataContextOption? value) => NotifyContextChanged();

    private void NotifyContextChanged()
    {
        OnPropertyChanged(nameof(RunId));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(IsHistory));
        OnPropertyChanged(nameof(IsReadOnly));
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatDate(string value) => DateTimeOffset.TryParse(value, out var parsed)
        ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : value;
}

public sealed record DataContextOption(long? RunId, string DisplayText, string Detail, bool IsReadOnly);
