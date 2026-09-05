using EmsScout.Application;
using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Application.Quality;

namespace EmsScout.Tests;

public sealed class DashboardOverviewServiceTests
{
    [Fact]
    public async Task LoadsSelectedHistoricalRunThroughDeviceQuery()
    {
        var repository = new CapturingDeviceRepository();
        var service = new DashboardOverviewService(
            repository,
            new EmptyQualityAuditService(),
            new EmptyRealtimeQualityAuditService(),
            new FailingRealtimeReconciliationService(),
            new EmptyCollectionRunRepository(),
            new FailingAreaGroupRepository());

        await service.LoadAsync(runId: 42);

        Assert.NotNull(repository.LastQuery);
        Assert.Equal(42, repository.LastQuery!.RunId);
    }

    private sealed class CapturingDeviceRepository : IDeviceReadRepository
    {
        public DeviceQuery? LastQuery { get; private set; }

        public Task<DeviceListResult> SearchAsync(DeviceQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(new DeviceListResult(0, [], DeviceFacets.From([])));
        }

        public Task<DeviceFilterOptions> LoadFilterOptionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeviceFilterOptions> LoadFilterOptionsAsync(DeviceQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyQualityAuditService : IQualityAuditService
    {
        public Task<QualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<QualityAuditReport?>(null);
    }

    private sealed class EmptyRealtimeQualityAuditService : IRealtimeQualityAuditService
    {
        public Task<RealtimeQualityAuditReport?> LoadLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<RealtimeQualityAuditReport?>(null);
    }

    private sealed class FailingRealtimeReconciliationService : IRealtimeReconciliationService
    {
        public Task<RealtimeReconciliationResult> AnalyzeAsync(RealtimeReconciliationQuery query, CancellationToken cancellationToken = default) =>
            Task.FromException<RealtimeReconciliationResult>(new InvalidOperationException("not used"));
    }

    private sealed class EmptyCollectionRunRepository : ICollectionRunRepository
    {
        public Task<IReadOnlyList<CollectionRunRecord>> ListAsync(int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CollectionRunRecord>>([]);

        public Task<CollectionRunRecord> SetAnomalyAsync(long runId, bool isAnomaly, string note, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CollectionRunRestoreResult> RestoreCurrentAsync(long runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CollectionRunDeleteResult> DeleteAsync(long runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FailingAreaGroupRepository : IAreaGroupRepository
    {
        public Task<AreaGroupSet> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<AreaGroupSet>(new InvalidOperationException("not used"));

        public Task<IReadOnlyList<AreaGroupTargetOption>> LoadTargetOptionsAsync(string building, string floorLabel, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AreaGroupRecord> SaveGroupAsync(AreaGroupEdit edit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteGroupAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AreaGroupItemRecord> SaveItemAsync(AreaGroupItemEdit edit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteItemAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FloorCatalogRecord>> LoadFloorsAsync(string building, bool includeDisabled = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FloorCatalogRecord> SaveFloorAsync(FloorCatalogEdit edit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteFloorAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
