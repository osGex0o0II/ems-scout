using EmsScout.Application.Quality;

namespace EmsScout.Desktop.ViewModels;

public sealed class CollectionIssueCategoryRow(CollectionIssueCategory category)
{
    public string Code { get; } = category.Code;
    public string Label { get; } = category.Label;
    public string Severity { get; } = category.Severity;
    public int Count { get; } = category.Count;
    public string CountText { get; } = category.Count.ToString("N0");
}
