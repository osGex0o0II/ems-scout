using System.IO.Compression;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class CustomGroupExportTests
{
    [Fact]
    public async Task DeviceQueryAndExportHonorCustomMonitorGroupFilter()
    {
        var databasePath = CreateDatabase();
        var repository = new SqliteDeviceReadRepository(databasePath);
        var exportService = new SqliteDeviceExportService(repository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-custom-group-export-tests", Guid.NewGuid().ToString("N"));

        var query = new DeviceQuery(MonitorGroupIds: "10");
        var result = await repository.SearchAsync(query);
        var export = await exportService.ExportAsync(query, output);

        Assert.Equal(2, result.Total);
        Assert.All(result.Rows, row => Assert.Equal("1号", row.Building));
        Assert.Contains(result.Rows, row => row.Name == "1-0101-KT");
        Assert.Contains(result.Rows, row => row.Name == "1-0102-KT");
        Assert.Equal(2, export.RowCount);
        UserDeviceWorkbookAssert.AssertShape(export);

        using var archive = ZipFile.OpenRead(export.Path);
        var devices = ReadEntry(archive, "xl/worksheets/sheet1.xml");
        Assert.Contains("1-0101-KT", devices);
        Assert.DoesNotContain("1-0201-KT", devices);
    }

    [Fact]
    public async Task GroupFilterIncludesMatchingVirtualDevicesAndExcludesVirtualDevicesOutsideItsScope()
    {
        var databasePath = CreateDatabase();
        var realtimeSource = CreateRealtimeSource();
        var repository = new SqliteDeviceReadRepository(databasePath, realtimeSource);
        var exportService = new SqliteDeviceExportService(repository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-custom-group-export-tests", Guid.NewGuid().ToString("N"));
        var query = new DeviceQuery(MonitorGroupIds: "10");

        var result = await repository.SearchAsync(query);
        var export = await exportService.ExportAsync(query, output);

        Assert.Equal(4, result.Total);
        Assert.Equal(result.Total, result.Rows.Count);
        Assert.Equal(result.Total, export.RowCount);
        Assert.Contains(result.Rows, row => row.Name == "GQ-VIRTUAL-IN-KT" && row.IsVirtual);
        Assert.Contains(result.Rows, row => row.Name == "GQ-VIRTUAL-OFFLINE-IN-KT" && row.IsVirtual);
        Assert.DoesNotContain(result.Rows, row => row.Name == "GQ-VIRTUAL-OUT-KT");
        Assert.DoesNotContain(result.Rows, row => row.Name == "GQ-VIRTUAL-OTHER-BUILDING-KT");
        Assert.Equal(2, realtimeSource.RequestedBuildings.Count);
        Assert.All(realtimeSource.RequestedBuildings, buildings => Assert.Equal(["1号"], buildings));

        using var archive = ZipFile.OpenRead(export.Path);
        var devices = ReadEntry(archive, "xl/worksheets/sheet1.xml");
        Assert.Contains("GQ-VIRTUAL-IN-KT", devices);
        Assert.Contains("GQ-VIRTUAL-OFFLINE-IN-KT", devices);
        Assert.DoesNotContain("GQ-VIRTUAL-OUT-KT", devices);
        Assert.DoesNotContain("GQ-VIRTUAL-OTHER-BUILDING-KT", devices);
    }

    [Fact]
    public async Task GroupPublicAreaAndCommunicationFiltersKeepListAndExcelInSync()
    {
        var databasePath = CreateDatabase();
        var realtimeSource = CreateRealtimeSource();
        var repository = new SqliteDeviceReadRepository(databasePath, realtimeSource);
        var exportService = new SqliteDeviceExportService(repository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-custom-group-export-tests", Guid.NewGuid().ToString("N"));
        var query = new DeviceQuery(
            CommunicationState: "离线",
            AreaType: "公区",
            MonitorGroupIds: "10");

        var result = await repository.SearchAsync(query);
        var export = await exportService.ExportAsync(query, output);

        var row = Assert.Single(result.Rows);
        Assert.Equal("GQ-VIRTUAL-OFFLINE-IN-KT", row.Name);
        Assert.True(row.IsVirtual);
        Assert.Equal("公区", row.AreaType);
        Assert.Equal("离线", row.CommunicationStatusText);
        Assert.Equal(result.Total, export.RowCount);
        Assert.All(realtimeSource.RequestedBuildings, buildings => Assert.Equal(["1号"], buildings));

        using var archive = ZipFile.OpenRead(export.Path);
        var devices = ReadEntry(archive, "xl/worksheets/sheet1.xml");
        Assert.Contains("GQ-VIRTUAL-OFFLINE-IN-KT", devices);
        Assert.DoesNotContain("GQ-VIRTUAL-IN-KT", devices);
        Assert.DoesNotContain("GQ-VIRTUAL-OUT-KT", devices);
    }

    [Fact]
    public async Task QueryAndExportUseSameMixedCustomGroupTargets()
    {
        var databasePath = CreateDatabase();
        var readRepository = new SqliteDeviceReadRepository(databasePath);
        var areaRepository = new SqliteAreaGroupRepository(() => databasePath);
        var exportService = new SqliteDeviceExportService(readRepository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-custom-group-export-tests", Guid.NewGuid().ToString("N"));

        await areaRepository.SaveItemAsync(new AreaGroupItemEdit(
            10,
            "sub_area",
            "1号",
            "2F",
            "2F B",
            string.Empty,
            "二层子区"));
        await areaRepository.SaveItemAsync(new AreaGroupItemEdit(
            10,
            "device",
            "2号",
            "1F",
            "1F A",
            "2-0101-KT",
            "二号楼单台"));

        var query = new DeviceQuery(MonitorGroupIds: "10");
        var result = await readRepository.SearchAsync(query);
        var export = await exportService.ExportAsync(query, output);

        Assert.Equal(4, result.Total);
        Assert.Equal(result.Total, export.RowCount);
        Assert.Equal(["1-0101-KT", "1-0102-KT", "1-0201-KT", "2-0101-KT"], result.Rows.Select(row => row.Name).Order().ToArray());
        UserDeviceWorkbookAssert.AssertShape(export);

        using var archive = ZipFile.OpenRead(export.Path);
        var devices = ReadEntry(archive, "xl/worksheets/sheet1.xml");
        Assert.Contains("1-0101-KT", devices);
        Assert.Contains("1-0102-KT", devices);
        Assert.Contains("1-0201-KT", devices);
        Assert.Contains("2-0101-KT", devices);
    }

    [Fact]
    public async Task EditedCustomGroupMemberImmediatelyChangesQueryAndExportScope()
    {
        var databasePath = CreateDatabase();
        var readRepository = new SqliteDeviceReadRepository(databasePath);
        var areaRepository = new SqliteAreaGroupRepository(() => databasePath);
        var exportService = new SqliteDeviceExportService(readRepository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-custom-group-export-tests", Guid.NewGuid().ToString("N"));
        var item = Assert.Single((await areaRepository.LoadAsync()).Items, item => item.GroupId == 10);

        await areaRepository.SaveItemAsync(new AreaGroupItemEdit(
            10,
            "device",
            "1号",
            "2F",
            "2F B",
            "1-0201-KT",
            "改为二层设备",
            item.Id));

        var query = new DeviceQuery(MonitorGroupIds: "10");
        var result = await readRepository.SearchAsync(query);
        var export = await exportService.ExportAsync(query, output);

        var row = Assert.Single(result.Rows);
        Assert.Equal("1-0201-KT", row.Name);
        Assert.Equal(result.Total, export.RowCount);
        UserDeviceWorkbookAssert.AssertShape(export);

        using var archive = ZipFile.OpenRead(export.Path);
        var devices = ReadEntry(archive, "xl/worksheets/sheet1.xml");
        Assert.Contains("1-0201-KT", devices);
        Assert.DoesNotContain("1-0101-KT", devices);
        Assert.DoesNotContain("1-0102-KT", devices);
    }

    [Fact]
    public async Task ExactDeviceMemberDoesNotExpandToSameNameDevicesInOtherAreas()
    {
        var databasePath = CreateDatabase();
        var readRepository = new SqliteDeviceReadRepository(databasePath);
        var areaRepository = new SqliteAreaGroupRepository(() => databasePath);
        var group = await areaRepository.SaveGroupAsync(new AreaGroupEdit(
            Id: null,
            Name: "同名设备精确组",
            AreaLabel: "同名",
            Description: "验证同名设备不扩大范围",
            Priority: "重点",
            Enabled: true));
        var exportService = new SqliteDeviceExportService(readRepository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-custom-group-export-tests", Guid.NewGuid().ToString("N"));

        await areaRepository.SaveItemAsync(new AreaGroupItemEdit(
            group.Id,
            "device",
            "1号",
            "3F",
            "3F C",
            "DUP-KT",
            "只选 3F C"));

        var query = new DeviceQuery(MonitorGroupIds: group.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = await readRepository.SearchAsync(query);
        var export = await exportService.ExportAsync(query, output);

        var row = Assert.Single(result.Rows);
        Assert.Equal("DUP-KT", row.Name);
        Assert.Equal("3F C", row.SubArea);
        Assert.Equal(result.Total, export.RowCount);
    }

    [Fact]
    public async Task DeletedMemberAndDeletedGroupStopMatchingQueryAndExport()
    {
        var databasePath = CreateDatabase();
        var readRepository = new SqliteDeviceReadRepository(databasePath);
        var areaRepository = new SqliteAreaGroupRepository(() => databasePath);
        var exportService = new SqliteDeviceExportService(readRepository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-custom-group-export-tests", Guid.NewGuid().ToString("N"));
        var item = Assert.Single((await areaRepository.LoadAsync()).Items, item => item.GroupId == 10);

        await areaRepository.DeleteItemAsync(item.Id);
        var afterItemDelete = await readRepository.SearchAsync(new DeviceQuery(MonitorGroupIds: "10"));
        var exportAfterItemDelete = await exportService.ExportAsync(new DeviceQuery(MonitorGroupIds: "10"), output);

        Assert.Equal(0, afterItemDelete.Total);
        Assert.Equal(0, exportAfterItemDelete.RowCount);

        await areaRepository.SaveItemAsync(new AreaGroupItemEdit(
            10,
            "floor",
            "1号",
            "1F",
            string.Empty,
            string.Empty,
            "恢复一层"));
        await areaRepository.DeleteGroupAsync(10);
        var afterGroupDelete = await readRepository.SearchAsync(new DeviceQuery(MonitorGroupIds: "10"));
        var exportAfterGroupDelete = await exportService.ExportAsync(new DeviceQuery(MonitorGroupIds: "10"), output);

        Assert.Equal(0, afterGroupDelete.Total);
        Assert.Equal(0, exportAfterGroupDelete.RowCount);
    }

    [Fact]
    public async Task DisabledCustomGroupDoesNotFilterQueryOrExport()
    {
        var databasePath = CreateDatabase();
        var readRepository = new SqliteDeviceReadRepository(databasePath);
        var areaRepository = new SqliteAreaGroupRepository(() => databasePath);
        var exportService = new SqliteDeviceExportService(readRepository);
        var output = Path.Combine(Path.GetTempPath(), "ems-scout-custom-group-export-tests", Guid.NewGuid().ToString("N"));

        await areaRepository.SaveGroupAsync(new AreaGroupEdit(
            Id: 10,
            Name: "巡检组",
            AreaLabel: "巡检",
            Description: "临时巡检",
            Priority: "重点",
            Enabled: false));

        var query = new DeviceQuery(MonitorGroupIds: "10");
        var result = await readRepository.SearchAsync(query);
        var export = await exportService.ExportAsync(query, output);
        var group = Assert.Single((await areaRepository.LoadAsync()).Groups, group => group.Id == 10);

        Assert.False(group.Enabled);
        Assert.Equal(1, group.ItemCount);
        Assert.Equal(0, group.Total);
        Assert.Equal(0, result.Total);
        Assert.Equal(0, export.RowCount);
    }

    private static string CreateDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-device-group-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ac.db");
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE sub_areas (
                id INTEGER PRIMARY KEY,
                building TEXT NOT NULL,
                floor REAL,
                text TEXT NOT NULL,
                sub_idx INTEGER NOT NULL DEFAULT 0,
                x REAL,
                y REAL
            );
            CREATE TABLE pages (
                id INTEGER PRIMARY KEY,
                sub_area_id INTEGER NOT NULL,
                page_name TEXT NOT NULL,
                layout TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE cards (
                id INTEGER PRIMARY KEY,
                page_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                switch TEXT,
                mode TEXT,
                indoor TEXT,
                set_temp TEXT,
                fan TEXT,
                indicator TEXT,
                comm TEXT
            );
            CREATE TABLE monitor_groups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                area_label TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                priority TEXT NOT NULL DEFAULT '重点',
                group_kind TEXT NOT NULL DEFAULT 'custom',
                system_key TEXT,
                locked INTEGER NOT NULL DEFAULT 0,
                enabled INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE monitor_group_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id INTEGER NOT NULL,
                target_type TEXT NOT NULL DEFAULT 'floor',
                building TEXT NOT NULL,
                floor_label TEXT,
                floor_value REAL,
                sub_area_text TEXT,
                card_name TEXT,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE realtime_match_overrides (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                building TEXT NOT NULL,
                dev_id TEXT NOT NULL DEFAULT '',
                floor_label TEXT NOT NULL DEFAULT '',
                sub_area TEXT NOT NULL DEFAULT '',
                page_name TEXT NOT NULL DEFAULT 'default',
                realtime_name TEXT NOT NULL DEFAULT '',
                action TEXT NOT NULL DEFAULT 'classify_only',
                target_card_id INTEGER,
                zuo_override TEXT,
                area_type_override TEXT,
                note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            INSERT INTO sub_areas (id, building, floor, text, sub_idx, x, y) VALUES
                (1, '1号', 1, '1F A', 1, 100, 100),
                (2, '1号', 2, '2F B', 2, 100, 200),
                (3, '2号', 1, '1F A', 1, 100, 100),
                (4, '1号', 3, '3F C', 3, 100, 300),
                (5, '1号', 4, '4F D', 4, 100, 400);
            INSERT INTO pages (id, sub_area_id, page_name, layout) VALUES
                (1, 1, 'default', 'grid'),
                (2, 2, 'default', 'grid'),
                (3, 3, 'default', 'grid'),
                (4, 4, 'default', 'grid'),
                (5, 5, 'default', 'grid');
            INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm) VALUES
                (1, 1, '1-0101-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                (2, 1, '1-0102-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机'),
                (3, 2, '1-0201-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机'),
                (4, 3, '2-0101-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                (5, 4, 'DUP-KT', 'ON', '制冷', '26', '24', '中', 'red.png', '开机'),
                (6, 5, 'DUP-KT', 'OFF', '制冷', '25', '24', '中', 'green.png', '关机');
            INSERT INTO monitor_groups
                (id, name, area_label, description, priority, group_kind, locked, enabled)
            VALUES
                (10, '巡检组', '巡检', '临时巡检', '重点', 'custom', 0, 1);
            INSERT INTO monitor_group_items
                (group_id, target_type, building, floor_label, floor_value, sub_area_text, card_name, note)
            VALUES
                (10, 'floor', '1号', '1F', 1, NULL, NULL, '一层');
            INSERT INTO realtime_match_overrides
                (id, building, dev_id, floor_label, sub_area, page_name, realtime_name,
                 action, target_card_id, zuo_override, area_type_override, note)
            VALUES
                (101, '1号', 'virtual-in', '1F', '1F A', 'default', 'GQ-VIRTUAL-IN-KT',
                 'create_virtual', NULL, NULL, '公区', '组内虚拟设备'),
                (102, '1号', 'virtual-out', '2F', '2F B', 'default', 'GQ-VIRTUAL-OUT-KT',
                 'create_virtual', NULL, NULL, '公区', '同楼栋组外虚拟设备'),
                (103, '1号', 'virtual-offline-in', '1F', '1F A', 'default', 'GQ-VIRTUAL-OFFLINE-IN-KT',
                 'create_virtual', NULL, NULL, '公区', '组内离线虚拟设备'),
                (104, '2号', 'virtual-other-building', '1F', '1F A', 'default', 'GQ-VIRTUAL-OTHER-BUILDING-KT',
                 'create_virtual', NULL, NULL, '公区', '其他楼栋虚拟设备');
            """;
        command.ExecuteNonQuery();
        return path;
    }

    private static RecordingRealtimeSource CreateRealtimeSource()
    {
        return new RecordingRealtimeSource(
        [
            Realtime("virtual-in", "1号", 1, "1F A", "GQ-VIRTUAL-IN-KT", "开机"),
            Realtime("virtual-out", "1号", 2, "2F B", "GQ-VIRTUAL-OUT-KT", "开机"),
            Realtime("virtual-offline-in", "1号", 1, "1F A", "GQ-VIRTUAL-OFFLINE-IN-KT", "离线"),
            Realtime("virtual-other-building", "2号", 1, "1F A", "GQ-VIRTUAL-OTHER-BUILDING-KT", "开机"),
        ]);
    }

    private static RealtimeDetailRecord Realtime(
        string devId,
        string building,
        double floor,
        string subArea,
        string name,
        string communication)
    {
        var power = communication is "开机" or "关机" ? communication : string.Empty;
        var cardSwitch = power == "开机" ? "ON" : power == "关机" ? "OFF" : string.Empty;
        return new RealtimeDetailRecord(
            RowId: "row-" + devId,
            SourceFile: "test",
            SourceUpdatedAt: DateTimeOffset.Parse("2026-08-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Building: building,
            Floor: floor,
            SubArea: subArea,
            PageName: "default",
            Name: name,
            DevId: devId,
            MeterId: string.Empty,
            RtuId: string.Empty,
            FieldCount: 5,
            RealtimeTagCount: 5,
            RealtimeValidTagCount: 5,
            DefaultLike: false,
            Error: string.Empty,
            CardComm: communication,
            CardSwitch: cardSwitch,
            CardIndicator: string.Empty,
            Fields: new Dictionary<string, string>
            {
                ["当前开关机状态"] = power,
                ["室内温度"] = "26",
                ["设定温度"] = "24",
                ["设定风速"] = "中",
                ["系统模式设置"] = "制冷",
            },
            ValidFields: new Dictionary<string, bool>());
    }

    private sealed class RecordingRealtimeSource(IReadOnlyList<RealtimeDetailRecord> rows) : IRealtimeDetailSource
    {
        public List<IReadOnlyList<string>> RequestedBuildings { get; } = [];

        public Task<RealtimeDetailSet> LoadAsync(
            IReadOnlyList<string> buildings,
            CancellationToken cancellationToken = default)
        {
            RequestedBuildings.Add(buildings.ToArray());
            var requested = buildings.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new RealtimeDetailSet(
                rows.Where(row => requested.Contains(row.Building)).ToArray()));
        }
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
