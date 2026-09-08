using EmsScout.Application;
using EmsScout.Application.Collection;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Application.Quality;
using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class DashboardOverviewServiceTests
{
    [Fact]
    public async Task LoadsSelectedHistoricalRunThroughDeviceQuery()
    {
        var repository = new CapturingDeviceRepository();
        var service = new DashboardOverviewService(
            repository,
            new FailingAreaGroupRepository());

        await service.LoadAsync(runId: 42);

        Assert.NotNull(repository.LastQuery);
        Assert.Equal(42, repository.LastQuery!.RunId);
    }

    [Fact]
    public async Task ExcludesVirtualRealtimeRowsFromInventorySummary()
    {
        var regular = TestDevice(1, isVirtual: false);
        var virtualRow = TestDevice(-10, isVirtual: true);
        var repository = new CapturingDeviceRepository(
            new DeviceListResult(
                2,
                [regular, virtualRow],
                DeviceFacets.From([regular, virtualRow])));
        var service = new DashboardOverviewService(
            repository,
            new FailingAreaGroupRepository());

        var overview = await service.LoadAsync();

        Assert.Equal(1, overview.Summary.Total);
        Assert.Equal("1", overview.Metrics.Single(metric => metric.Label == "总设备数").Value);
        Assert.DoesNotContain("实时纳管", overview.Metrics.Single(metric => metric.Label == "总设备数").Detail);
    }

    [Fact]
    public async Task LabelsHistoricalInventoryMetricAsHistorical()
    {
        var repository = new CapturingDeviceRepository();
        var service = new DashboardOverviewService(
            repository,
            new FailingAreaGroupRepository());

        var overview = await service.LoadAsync(runId: 42);

        var totalMetric = overview.Metrics.Single(metric => metric.Label == "总设备数");
        Assert.Contains("历史批次", totalMetric.Detail);
        Assert.DoesNotContain("当前完整采集批次", totalMetric.Detail);
    }

    private static DeviceRecord TestDevice(long id, bool isVirtual)
    {
        return new DeviceRecord(
            Id: id,
            Building: "1号",
            Floor: 1,
            FloorLabel: "1F",
            SubArea: "1F A",
            X: null,
            Y: null,
            PageName: "1",
            Name: isVirtual ? "GQ-VIRTUAL-KT" : "1-0101-KT",
            Layout: "grid",
            SwitchState: "OFF",
            Mode: "制冷",
            IndoorTemperature: "26",
            SetTemperature: "25",
            Fan: "中",
            Indicator: string.Empty,
            CommunicationText: "关机",
            CommunicationState: DeviceCommunicationState.Stopped,
            IsVirtual: isVirtual);
    }

    private sealed class CapturingDeviceRepository(DeviceListResult? result = null) : IDeviceReadRepository
    {
        private readonly DeviceListResult _result = result ?? new DeviceListResult(0, [], DeviceFacets.From([]));

        public DeviceQuery? LastQuery { get; private set; }

        public Task<DeviceListResult> SearchAsync(DeviceQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(_result);
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

        public Task<CollectionRunComparison> CompareCurrentAsync(long runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
