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
            """;
        command.ExecuteNonQuery();
        return path;
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
