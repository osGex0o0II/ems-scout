using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Sqlite;

namespace EmsScout.Tests;

public sealed class SqliteDeviceReadRepositoryTests
{
    [Fact]
    public async Task SearchesCurrentDatabaseWithBaselineCounts()
    {
        var repository = new SqliteDeviceReadRepository(CurrentDatabasePath());

        var all = await repository.SearchAsync(new(Limit: 10));
        var building1 = await repository.SearchAsync(new(Building: "1号", Limit: 1));
        var offline = await repository.SearchAsync(new(CommunicationState: "离线", Limit: 1));
        var unknown = await repository.SearchAsync(new(CommunicationState: "未知", Limit: 10));

        Assert.Equal(6568, all.Total);
        Assert.Equal(10, all.Rows.Count);
        Assert.Equal(776, all.Facets.PublicArea);
        Assert.Equal(5792, all.Facets.PrivateArea);
        Assert.Equal(437, all.Facets.TemperatureIssues);
        Assert.Equal(1546, all.Facets.NeedsReview);
        Assert.Equal(1493, building1.Total);
        Assert.Equal(1527, offline.Total);
        Assert.Equal(2, unknown.Total);
        Assert.Equal("1号", all.Rows[0].Building);
        Assert.False(string.IsNullOrWhiteSpace(all.Rows[0].Name));
    }

    [Fact]
    public async Task LoadsFilterOptionsFromCurrentDatabase()
    {
        var repository = new SqliteDeviceReadRepository(CurrentDatabasePath());

        var options = await repository.LoadFilterOptionsAsync();

        Assert.Equal(6, options.Buildings.Count);
        Assert.Contains(options.Buildings, option => option.Value == "6号" && option.Count == 2480);
        Assert.Contains(options.CommunicationStates, option => option.Value == "关机" && option.Count == 3196);
        Assert.Contains(options.CommunicationStates, option => option.Value == "未知" && option.Count == 2);
        Assert.Equal(35, options.Floors.Count);
        Assert.Contains(options.Floors, option => option.Value == "B1F" && option.Count == 24);
        Assert.Contains(options.Floors, option => option.Value == "2.5F" && option.Count == 7);
        Assert.Contains(options.Zuos, option => option.Value == "A座" && option.Count == 703);
        Assert.Contains(options.Zuos, option => option.Value == "F座" && option.Count == 68);
        Assert.NotEmpty(options.SubAreas);
        Assert.NotEmpty(options.PageNames);
        Assert.Equal("default", options.PageNames[0].Value);
        Assert.Equal("默认页", options.PageNames[0].Label);
        Assert.True(options.PageNames.ToList().FindIndex(option => option.Value == "一页") <
                    options.PageNames.ToList().FindIndex(option => option.Value == "二页"));
        Assert.Equal("BM", options.PageNames[^1].Value);
        Assert.NotEmpty(options.Modes);
        Assert.NotEmpty(options.Fans);
        Assert.NotEmpty(options.SetTemperatures);
        Assert.NotEmpty(options.IndoorTemperatures);
        Assert.Empty(options.Tags);
    }

    [Fact]
    public async Task AppliesDataManagementPrimaryFieldFilters()
    {
        var repository = new SqliteDeviceReadRepository(CurrentDatabasePath());
        var baseline = await repository.SearchAsync(new(Limit: 2000));
        var sample = baseline.Rows.First(row =>
            !string.IsNullOrWhiteSpace(row.SubArea) &&
            !string.IsNullOrWhiteSpace(row.Name) &&
            !string.IsNullOrWhiteSpace(row.Mode) &&
            !string.IsNullOrWhiteSpace(row.Fan) &&
            !string.IsNullOrWhiteSpace(row.SetTemperature) &&
            !string.IsNullOrWhiteSpace(row.IndoorTemperature));
        var deviceNameNeedle = sample.Name[..Math.Min(sample.Name.Length, 6)];

        var byPageName = await repository.SearchAsync(new(PageName: sample.PageName, Limit: 50));
        var byDeviceName = await repository.SearchAsync(new(DeviceName: deviceNameNeedle, Limit: 50));
        var byMode = await repository.SearchAsync(new(Mode: sample.Mode, Limit: 50));
        var byFan = await repository.SearchAsync(new(Fan: sample.Fan, Limit: 50));
        var bySetTemperature = await repository.SearchAsync(new(SetTemperature: sample.SetTemperature, Limit: 50));
        var byIndoorTemperature = await repository.SearchAsync(new(IndoorTemperature: sample.IndoorTemperature, Limit: 50));
        var byCommunication = await repository.SearchAsync(new(CommunicationState: sample.CommunicationText, Limit: 50));

        Assert.NotEmpty(byPageName.Rows);
        Assert.All(byPageName.Rows, row => Assert.Equal(sample.PageName, row.PageName));
        Assert.NotEmpty(byDeviceName.Rows);
        Assert.All(byDeviceName.Rows, row => Assert.Contains(deviceNameNeedle, row.Name, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(byMode.Rows);
        Assert.All(byMode.Rows, row => Assert.Equal(sample.Mode, row.Mode));
        Assert.NotEmpty(byFan.Rows);
        Assert.All(byFan.Rows, row => Assert.Equal(sample.Fan, row.Fan));
        Assert.NotEmpty(bySetTemperature.Rows);
        Assert.All(bySetTemperature.Rows, row => Assert.Equal(sample.SetTemperature, row.SetTemperature));
        Assert.NotEmpty(byIndoorTemperature.Rows);
        Assert.All(byIndoorTemperature.Rows, row => Assert.Equal(sample.IndoorTemperature, row.IndoorTemperature));
        Assert.NotEmpty(byCommunication.Rows);
        Assert.All(byCommunication.Rows, row => Assert.Equal(sample.CommunicationText, row.CommunicationText));
    }

    [Fact]
    public async Task AppliesDataManagementCombinedFieldFilters()
    {
        var repository = new SqliteDeviceReadRepository(CurrentDatabasePath());
        var baseline = await repository.SearchAsync(new(Building: "6号", Limit: 2000));
        var sample = baseline.Rows.First(row =>
            !string.IsNullOrWhiteSpace(row.Building) &&
            !string.IsNullOrWhiteSpace(row.Zuo) &&
            !string.IsNullOrWhiteSpace(row.FloorLabel) &&
            !string.IsNullOrWhiteSpace(row.SubArea) &&
            !string.IsNullOrWhiteSpace(row.Name) &&
            !string.IsNullOrWhiteSpace(row.CommunicationText) &&
            !string.IsNullOrWhiteSpace(row.Mode) &&
            !string.IsNullOrWhiteSpace(row.Fan) &&
            !string.IsNullOrWhiteSpace(row.SetTemperature) &&
            !string.IsNullOrWhiteSpace(row.IndoorTemperature));

        var query = new DeviceQuery(
            Building: sample.Building,
            CommunicationState: sample.CommunicationText,
            Floor: sample.FloorLabel,
            PageName: sample.PageName,
            DeviceName: sample.Name,
            Zuo: sample.Zuo,
            Mode: sample.Mode,
            Fan: sample.Fan,
            SetTemperature: sample.SetTemperature,
            IndoorTemperature: sample.IndoorTemperature,
            AreaType: sample.AreaType,
            Limit: 50);
        var matching = await repository.SearchAsync(query);
        var wrongMode = await repository.SearchAsync(query with { Mode = "__missing_mode__" });

        Assert.NotEmpty(matching.Rows);
        Assert.All(matching.Rows, row =>
        {
            Assert.Equal(sample.Building, row.Building);
            Assert.Equal(sample.CommunicationText, row.CommunicationText);
            Assert.Equal(sample.FloorLabel, row.FloorLabel);
            Assert.Equal(sample.PageName, row.PageName);
            Assert.Contains(sample.Name, row.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sample.Zuo, row.Zuo);
            Assert.Equal(sample.Mode, row.Mode);
            Assert.Equal(sample.Fan, row.Fan);
            Assert.Equal(sample.SetTemperature, row.SetTemperature);
            Assert.Equal(sample.IndoorTemperature, row.IndoorTemperature);
            Assert.Equal(sample.AreaType, row.AreaType);
        });
        Assert.Equal(0, wrongMode.Total);
    }

    [Fact]
    public async Task FilterOptionsRespectFinalDataManagementFilters()
    {
        var repository = new SqliteDeviceReadRepository(CurrentDatabasePath(), new CurrentRealtimeSource());

        var publicAreaQuery = new DeviceQuery(AreaType: "公区", Limit: 1);
        var publicAreaResult = await repository.SearchAsync(publicAreaQuery);
        var publicAreaOptions = await repository.LoadFilterOptionsAsync(publicAreaQuery);

        Assert.Equal(publicAreaResult.Total, publicAreaOptions.Buildings.Sum(option => option.Count));

        var zuoSample = (await repository.SearchAsync(new DeviceQuery(Building: "6号", Limit: 2000))).Rows.First(row =>
            !string.IsNullOrWhiteSpace(row.Building) &&
            !string.IsNullOrWhiteSpace(row.Zuo));
        var zuoQuery = new DeviceQuery(Building: zuoSample.Building, Zuo: zuoSample.Zuo, Limit: 1);
        var zuoResult = await repository.SearchAsync(zuoQuery);
        var zuoOptions = await repository.LoadFilterOptionsAsync(zuoQuery);
        var zuo = Assert.Single(zuoOptions.Zuos);
        Assert.Equal(zuoSample.Zuo, zuo.Value);
        Assert.Equal(zuoResult.Total, zuo.Count);

        var missingRealtimeQuery = new DeviceQuery(RealtimeLock: "无实时数据", Limit: 1);
        var missingRealtimeResult = await repository.SearchAsync(missingRealtimeQuery);
        var missingRealtimeOptions = await repository.LoadFilterOptionsAsync(missingRealtimeQuery);
        var missingRealtime = Assert.Single(missingRealtimeOptions.RealtimeLocks ?? [], option => option.Value == "无实时数据");
        Assert.Equal(missingRealtimeResult.Total, missingRealtime.Count);
        Assert.DoesNotContain(missingRealtimeOptions.RealtimeLocks ?? [], option => option.Value != "无实时数据");
    }

    [Fact]
    public async Task AppliesNativeAreaAndQuickFilters()
    {
        var repository = new SqliteDeviceReadRepository(CurrentDatabasePath());

        var publicArea = await repository.SearchAsync(new(AreaType: "公区", Limit: 1));
        var privateArea = await repository.SearchAsync(new(AreaType: "非公区", Limit: 1));
        var needsReview = await repository.SearchAsync(new(QuickFilter: "needs_review", Limit: 1));
        var tempAbnormal = await repository.SearchAsync(new(QuickFilter: "temp_abnormal", Limit: 1));
        var normal = await repository.SearchAsync(new(QuickFilter: "normal", Limit: 1));

        Assert.Equal(776, publicArea.Total);
        Assert.Equal(5792, privateArea.Total);
        Assert.Equal(1546, needsReview.Total);
        Assert.Equal(437, tempAbnormal.Total);
        Assert.Equal(5022, normal.Total);
        Assert.Equal("需排查", needsReview.Rows[0].Health.Label);
    }

    [Fact]
    public async Task AppliesRealtimeDetailFieldFilters()
    {
        var repository = new SqliteDeviceReadRepository(CurrentDatabasePath(), new CurrentRealtimeSource());

        var poweredOn = await repository.SearchAsync(new(RealtimePower: "开机", Limit: 50));
        var locked = await repository.SearchAsync(new(RealtimeLock: "开启", Limit: 50));
        var cooling = await repository.SearchAsync(new(RealtimeMode: "制冷", Limit: 50));
        var modbus = await repository.SearchAsync(new(RealtimeModbus: "10", Limit: 50));
        var options = await repository.LoadFilterOptionsAsync();

        Assert.True(poweredOn.Total > 0);
        Assert.All(poweredOn.Rows, row => Assert.Equal("开机", row.Realtime?.PowerState));
        Assert.True(locked.Total > 0);
        Assert.All(locked.Rows, row => Assert.Equal("开启", row.Realtime?.LockState));
        Assert.True(cooling.Total > 0);
        Assert.All(cooling.Rows, row => Assert.Equal("制冷", row.Realtime?.Mode));
        Assert.True(modbus.Total > 0);
        Assert.All(modbus.Rows, row => Assert.Contains("10", row.Realtime?.ModbusAddress ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options.RealtimePowers ?? [], option => option.Value == "开机");
        Assert.Contains(options.RealtimeModes ?? [], option => option.Value == "制冷");
        Assert.Contains(options.RealtimeFans ?? [], option => option.Value == "自动");
        Assert.Contains(options.RealtimeLocks ?? [], option => option.Value == "开启");
        Assert.Contains(options.RealtimeSystemTypes ?? [], option => option.Value == "两管冷暖");
    }

    [Fact]
    public async Task SortsBeforePaging()
    {
        var repository = new SqliteDeviceReadRepository(CurrentDatabasePath());

        var byName = await repository.SearchAsync(new(SortBy: "name", Limit: 5));
        var byFloorDesc = await repository.SearchAsync(new(SortBy: "floor", SortDescending: true, Limit: 5));

        Assert.Equal(
            byName.Rows.Select(row => row.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            byName.Rows.Select(row => row.Name));
        Assert.True(byFloorDesc.Rows[0].Floor >= byFloorDesc.Rows[1].Floor);
    }

    [Fact]
    public async Task SearchesHistoricalRunSnapshotWithoutRestoringCurrentDatabase()
    {
        var repository = new SqliteDeviceReadRepository(CurrentDatabasePath());
        var runId = await LatestRunWithSnapshotAsync();

        var historical = await repository.SearchAsync(new DeviceQuery(RunId: runId, Limit: 5));
        var historicalOptions = await repository.LoadFilterOptionsAsync(new DeviceQuery(RunId: runId));
        var current = await repository.SearchAsync(new DeviceQuery(Limit: 1));

        Assert.True(historical.Total > 0);
        Assert.True(historical.Rows.Count > 0);
        Assert.All(historical.Rows, row => Assert.False(row.HasRealtime));
        Assert.Equal(historical.Total, historical.Facets.Total);
        Assert.Contains(historicalOptions.Buildings, option => option.Value == historical.Rows[0].Building);
        Assert.Equal(6568, current.Total);
    }

    private static string CurrentDatabasePath()
    {
        return ProductionDataSnapshot.DatabasePath;
    }

    private static async Task<long> LatestRunWithSnapshotAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={CurrentDatabasePath()};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cr.id
            FROM collection_runs cr
            WHERE EXISTS (SELECT 1 FROM run_cards rc WHERE rc.run_id = cr.id)
            ORDER BY datetime(cr.completed_at) DESC, cr.id DESC
            LIMIT 1
            """;
        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class CurrentRealtimeSource : EmsScout.Application.Devices.IRealtimeDetailSource
    {
        public Task<EmsScout.Application.Devices.RealtimeDetailSet> LoadAsync(
            IReadOnlyList<string> buildings,
            CancellationToken cancellationToken = default)
        {
            var root = ProductionDataSnapshot.RepositoryRoot;
            var source = new EmsScout.Infrastructure.Realtime.RealtimeLatestJsonSource(root, Path.Combine(root, "out"));
            return source.LoadAsync(buildings, cancellationToken);
        }
    }
}
