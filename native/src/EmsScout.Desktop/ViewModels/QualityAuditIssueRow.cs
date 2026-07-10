using EmsScout.Application.Quality;

namespace EmsScout.Desktop.ViewModels;

public sealed class QualityAuditIssueRow(QualityAuditIssue issue)
{
    public string Severity { get; } = issue.Severity;

    public string Code { get; } = issue.Code;

    public int Count { get; } = issue.Count;

    public string CountText { get; } = issue.Count.ToString("N0");

    public string Message { get; } = issue.Message;

    public string Label { get; } = issue.Label;
}
