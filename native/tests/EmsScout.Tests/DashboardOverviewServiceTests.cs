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
        var collectedAt = new DateTimeOffset(2026, 9, 8, 8, 41, 40, TimeSpan.FromHours(8));
        var regular = TestDevice(1, isVirtual: false, collectedAt);
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
        Assert.Equal("2026-09-08 08:41:40", overview.Metrics.Single(metric => metric.Label == "总设备数").Detail);
    }

    [Fact]
    public async Task DisplaysLatestCollectedAtForSelectedInventoryMetric()
    {
        var collectedAt = DateTimeOffset.Parse("2026-09-09T08:09:10+08:00");
        var device = TestDevice(42, isVirtual: false, collectedAt);
        var repository = new CapturingDeviceRepository(
            new DeviceListResult(1, [device], DeviceFacets.From([device])));
        var service = new DashboardOverviewService(
            repository,
            new FailingAreaGroupRepository());

        var overview = await service.LoadAsync(runId: 42);

        var totalMetric = overview.Metrics.Single(metric => metric.Label == "总设备数");
        Assert.Equal(collectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), totalMetric.Detail);
    }

    [Fact]
    public async Task UsesFallbackWhenSelectedInventoryHasNoCollectionTime()
    {
        var repository = new CapturingDeviceRepository();
        var service = new DashboardOverviewService(
            repository,
            new FailingAreaGroupRepository());

        var overview = await service.LoadAsync(runId: 42);

        var totalMetric = overview.Metrics.Single(metric => metric.Label == "总设备数");
        Assert.Equal("暂无采集时间", totalMetric.Detail);
        Assert.DoesNotContain("历史批次", totalMetric.Detail);
    }

    [Fact]
    public async Task UsesPercentagesForEveryStatusMetricDetail()
    {
        var devices = new[]
        {
            TestDevice(1, isVirtual: false, state: DeviceCommunicationState.Running),
            TestDevice(2, isVirtual: false, state: DeviceCommunicationState.Stopped),
            TestDevice(3, isVirtual: false, state: DeviceCommunicationState.Offline),
            TestDevice(4, isVirtual: false, state: DeviceCommunicationState.Unknown),
        };
        var repository = new CapturingDeviceRepository(
            new DeviceListResult(devices.Length, devices, DeviceFacets.From(devices)));
        var service = new DashboardOverviewService(
            repository,
            new FailingAreaGroupRepository());

        var overview = await service.LoadAsync();

        Assert.All(
            overview.Metrics.Where(metric => metric.Label is "开机" or "关机" or "离线" or "未知"),
            metric => Assert.Equal("25.0%", metric.Detail));
    }

    [Fact]
    public async Task CarriesRealtimeUnavailabilityIntoOverviewWhenOnlineRowsHaveNoDetails()
    {
        var devices = new[]
        {
            TestDevice(1, isVirtual: false, state: DeviceCommunicationState.Running),
            TestDevice(2, isVirtual: false, state: DeviceCommunicationState.Stopped),
        };
        var repository = new CapturingDeviceRepository(
            new DeviceListResult(devices.Length, devices, DeviceFacets.From(devices)));
        var service = new DashboardOverviewService(
            repository,
            new FailingAreaGroupRepository());

        var overview = await service.LoadAsync();

        Assert.Equal(DashboardRealtimeAvailability.Unavailable, overview.RealtimeAvailability);
        Assert.Contains("不可用", overview.RealtimeStatusText);
    }

    private static DeviceRecord TestDevice(
        long id,
        bool isVirtual,
        DateTimeOffset? collectedAt = null,
        DeviceCommunicationState state = DeviceCommunicationState.Stopped)
    {
        var (communicationText, switchState) = state switch
        {
            DeviceCommunicationState.Running => ("开机", "ON"),
            DeviceCommunicationState.Offline => ("离线", "-"),
            DeviceCommunicationState.Unknown => ("未知", "-"),
            _ => ("关机", "OFF"),
        };
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
            SwitchState: switchState,
            Mode: "制冷",
            IndoorTemperature: "26",
            SetTemperature: "25",
            Fan: "中",
            Indicator: string.Empty,
            CommunicationText: communicationText,
            CommunicationState: state,
            IsVirtual: isVirtual,
            CollectedAt: collectedAt);
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
        public Task<IReadOnlyList<CollectionRunRecord>> ListAsync(int? limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CollectionRunRecord>>([]);

        public Task<RunDeleteImpact> GetDeleteImpactAsync(long runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CollectionRunDeleteResult>> DeleteManyAsync(IReadOnlyList<long> runIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

        public Task<AreaGroupRecord> SaveGroupAsync(AreaGroupEdit edit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AreaGroupRecord> SaveConfigurationAsync(AreaGroupEdit edit, IReadOnlyList<AreaGroupRuleEdit> rules, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteGroupAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FloorCatalogRecord>> LoadFloorsAsync(string building, bool includeDisabled = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FloorCatalogRecord> SaveFloorAsync(FloorCatalogEdit edit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteFloorAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AreaGroupRuleRecord> SaveRuleAsync(AreaGroupRuleEdit edit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteRuleAsync(long id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AreaGroupTransferDocument> ExportAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ImportAsync(AreaGroupTransferDocument document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
