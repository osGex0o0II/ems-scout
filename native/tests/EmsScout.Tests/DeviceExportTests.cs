using EmsScout.Application.Devices;
using EmsScout.Domain;
using EmsScout.Infrastructure.Sqlite;
using EmsScout.Legacy;

namespace EmsScout.Tests;

public sealed class DeviceExportTests
{
    [Fact]
    public async Task ExportsCurrentFilteredDeviceWorkbook()
    {
        var exportService = CurrentExportService();
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-device-export-tests", Guid.NewGuid().ToString("N"));

        var export = await exportService.ExportAsync(new DeviceQuery(), output);

        Assert.True(File.Exists(export.Path), $"Missing export: {export.Path}");
        Assert.Matches(@"^数据管理筛选结果_\d{8}_\d{6}\.xlsx$", export.FileName);
        Assert.Equal("xlsx", export.Format);
        Assert.Equal(6570, export.RowCount);
        Assert.Equal(6570, export.Facets.Total);
        Assert.Equal(777, export.Facets.PublicArea);
        Assert.Equal(5793, export.Facets.PrivateArea);
        Assert.Equal(2, export.Facets.VirtualManaged);
        UserDeviceWorkbookAssert.AssertShape(export);
    }

    [Fact]
    public async Task DeviceWorkbookHonorsCurrentWorkbenchAreaFilters()
    {
        var exportService = CurrentExportService();
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-device-export-tests", Guid.NewGuid().ToString("N"));

        var export = await exportService.ExportAsync(new DeviceQuery(AreaType: "公区"), output);

        Assert.Equal(777, export.RowCount);
        Assert.Equal(777, export.Facets.PublicArea);
        UserDeviceWorkbookAssert.AssertShape(export);
        var rows = UserDeviceWorkbookAssert.ReadRows(export.Path);
        Assert.All(rows.Skip(1), row => Assert.Equal("公区", row[5]));
    }

    [Fact]
    public async Task DeviceWorkbookMapsAllUserColumnsInOrder()
    {
        var repository = CurrentRepository();
        var exportService = new SqliteDeviceExportService(repository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-device-export-tests", Guid.NewGuid().ToString("N"));
        var sample = (await repository.SearchAsync(new DeviceQuery(Building: "1号", Limit: 500))).Rows.First(row =>
            !string.IsNullOrWhiteSpace(row.Name) &&
            !string.IsNullOrWhiteSpace(row.FloorLabel) &&
            !string.IsNullOrWhiteSpace(row.SubArea) &&
            !string.IsNullOrWhiteSpace(row.CommunicationText) &&
            !string.IsNullOrWhiteSpace(row.Mode) &&
            !string.IsNullOrWhiteSpace(row.Fan) &&
            !string.IsNullOrWhiteSpace(row.SetTemperature) &&
            !string.IsNullOrWhiteSpace(row.IndoorTemperature));

        var export = await exportService.ExportAsync(
            new DeviceQuery(Building: sample.Building, DeviceName: sample.Name),
            output);

        UserDeviceWorkbookAssert.AssertShape(export);
        var rows = UserDeviceWorkbookAssert.ReadRows(export.Path);
        var row = Assert.Single(rows.Skip(1), row =>
            row[0] == sample.Building &&
            row[2] == sample.FloorLabel &&
            row[3] == sample.PageLabel &&
            row[4] == sample.Name);
        Assert.Equal(sample.Building, row[0]);
        Assert.Equal(sample.Zuo ?? string.Empty, row[1]);
        Assert.Equal(sample.FloorLabel, row[2]);
        Assert.Equal(sample.PageLabel, row[3]);
        Assert.Equal(sample.Name, row[4]);
        Assert.Equal(sample.AreaType, row[5]);
        Assert.Equal(sample.CommunicationText, row[6]);
        Assert.Equal(sample.Mode, row[7]);
        Assert.Equal(sample.Fan, row[8]);
        Assert.Equal(sample.SetTemperature, row[9]);
        Assert.Equal(sample.IndoorTemperature, row[10]);
        Assert.Equal(ExportLockText(sample), row[11]);
        Assert.Equal(12, row.Count);
    }

    [Fact]
    public async Task DeviceWorkbookMapsDeterministicRowsToTwelveUserColumns()
    {
        var rows = new[]
        {
            Device(
                id: 1,
                building: "1号",
                floorLabel: "1F",
                subArea: "1F A",
                name: "1-0101-KT",
                communication: "关机",
                areaType: "公区",
                zuo: "A座",
                realtimeLock: "开启"),
            Device(
                id: 2,
                building: "2号",
                floorLabel: "2F",
                subArea: "2F B",
                name: "2-0201-KT",
                communication: "开机",
                areaType: "非公区",
                zuo: "B座",
                realtimeLock: null),
            Device(
                id: 3,
                building: "3号",
                floorLabel: "3F",
                subArea: "3F C",
                name: "3-0301-KT",
                communication: "",
                areaType: "未匹配",
                zuo: "",
                realtimeLock: ""),
        };
        var exportService = new SqliteDeviceExportService(new FakeDeviceReadRepository(rows));
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-device-export-tests", Guid.NewGuid().ToString("N"));

        var export = await exportService.ExportAsync(new DeviceQuery(), output);

        UserDeviceWorkbookAssert.AssertShape(export);
        var exportedRows = UserDeviceWorkbookAssert.ReadRows(export.Path);
        Assert.Equal(
            ["1号", "A座", "1F", "默认页", "1-0101-KT", "公区", "关机", "制冷", "中", "25", "26", "开启"],
            exportedRows[1]);
        Assert.Equal(
            ["2号", "B座", "2F", "默认页", "2-0201-KT", "非公区", "开机", "制冷", "中", "25", "26", "无实时数据"],
            exportedRows[2]);
        Assert.Equal(
            ["3号", "", "3F", "默认页", "3-0301-KT", "未匹配", "未知", "制冷", "中", "25", "26", "未知"],
            exportedRows[3]);
    }

    [Fact]
    public async Task DeviceWorkbookUsesSingleRepositorySnapshot()
    {
        var rows = new[]
        {
            Device(
                id: 1,
                building: "1号",
                floorLabel: "1F",
                subArea: "1F A",
                name: "1-0101-KT",
                communication: "关机",
                areaType: "公区",
                zuo: "A座",
                realtimeLock: "开启"),
        };
        var repository = new FakeDeviceReadRepository(rows);
        var exportService = new SqliteDeviceExportService(repository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-device-export-tests", Guid.NewGuid().ToString("N"));

        await exportService.ExportAsync(new DeviceQuery(Building: "1号"), output);

        var query = Assert.Single(repository.SearchQueries);
        Assert.Equal(50000, query.Limit);
        Assert.Equal(0, query.Offset);
        Assert.Equal("1号", query.Building);
    }

    [Fact]
    public async Task RejectsHistoryRunExport()
    {
        var exportService = CurrentExportService();
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-device-export-tests", Guid.NewGuid().ToString("N"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exportService.ExportAsync(new DeviceQuery(RunId: 123), output));

        Assert.Contains("历史批次", error.Message);
        Assert.False(Directory.Exists(output));
    }

    private static SqliteDeviceExportService CurrentExportService()
    {
        return new SqliteDeviceExportService(CurrentRepository());
    }

    private static SqliteDeviceReadRepository CurrentRepository()
    {
        var root = LocateRepositoryRoot();
        return new SqliteDeviceReadRepository(
            Path.Combine(root, "out", "ac.db"),
            new RealtimeLatestJsonSource(root, Path.Combine(root, "out")));
    }

    private static string ExportLockText(DeviceRecord row)
    {
        return row.Realtime is null
            ? "无实时数据"
            : string.IsNullOrWhiteSpace(row.Realtime.LockState) ? "未知" : row.Realtime.LockState;
    }

    private static DeviceRecord Device(
        long id,
        string building,
        string floorLabel,
        string subArea,
        string name,
        string communication,
        string areaType,
        string zuo,
        string? realtimeLock)
    {
        return new DeviceRecord(
            Id: id,
            Building: building,
            Floor: null,
            FloorLabel: floorLabel,
            SubArea: subArea,
            X: null,
            Y: null,
            PageName: "default",
            Name: name,
            Layout: "grid",
            SwitchState: communication == "开机" ? "ON" : "OFF",
            Mode: "制冷",
            IndoorTemperature: "26",
            SetTemperature: "25",
            Fan: "中",
            Indicator: "",
            CommunicationText: communication,
            CommunicationState: DeviceCommunicationStateParser.Parse(communication),
            Realtime: realtimeLock is null ? null : Realtime(building, floorLabel, subArea, name, realtimeLock),
            AreaTypeOverride: areaType,
            Zuo: zuo);
    }

    private static RealtimeDetailRecord Realtime(
        string building,
        string floorLabel,
        string subArea,
        string name,
        string lockState)
    {
        return new RealtimeDetailRecord(
            RowId: "rt-" + name,
            SourceFile: "test",
            SourceUpdatedAt: DateTimeOffset.UnixEpoch,
            Building: building,
            Floor: null,
            SubArea: subArea,
            PageName: "default",
            Name: name,
            DevId: "dev-" + name,
            MeterId: string.Empty,
            RtuId: string.Empty,
            FieldCount: 1,
            RealtimeTagCount: 1,
            RealtimeValidTagCount: 1,
            DefaultLike: false,
            Error: string.Empty,
            CardComm: string.Empty,
            CardSwitch: string.Empty,
            CardIndicator: string.Empty,
            Fields: new Dictionary<string, string>
            {
                ["集控锁定"] = lockState,
            },
            ValidFields: new Dictionary<string, bool>
            {
                ["集控锁定"] = true,
            });
    }

    private sealed class FakeDeviceReadRepository(IReadOnlyList<DeviceRecord> rows) : IDeviceReadRepository
    {
        public List<DeviceQuery> SearchQueries { get; } = [];

        public Task<DeviceListResult> SearchAsync(DeviceQuery query, CancellationToken cancellationToken = default)
        {
            SearchQueries.Add(query);
            var page = rows
                .Skip(Math.Max(0, query.Offset))
                .Take(Math.Clamp(query.Limit, 1, 50000))
                .ToArray();
            return Task.FromResult(new DeviceListResult(rows.Count, page, DeviceFacets.From(rows)));
        }

        public Task<DeviceFilterOptions> LoadFilterOptionsAsync(CancellationToken cancellationToken = default)
        {
            return LoadFilterOptionsAsync(new DeviceQuery(), cancellationToken);
        }

        public Task<DeviceFilterOptions> LoadFilterOptionsAsync(
            DeviceQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeviceFilterOptions([], [], [], [], [], [], [], [], [], [], [], []));
        }
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "out")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }
}
