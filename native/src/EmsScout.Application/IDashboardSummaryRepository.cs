using EmsScout.Domain;

namespace EmsScout.Application;

public interface IDashboardSummaryRepository
{
    Task<DashboardSummaryResult> LoadAsync(
        long? runId,
        CancellationToken cancellationToken = default);
}

public sealed record DashboardSummaryResult(
    FleetSummary Summary,
    DateTimeOffset? SourceUpdatedAt,
    string Revision);
