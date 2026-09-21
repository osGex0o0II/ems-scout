using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class DeviceReadPerformanceContractTests
{
    [Fact]
    public async Task HistoricalAreaChoicesRetainSnapshotIdentityAfterCurrentGroupsChange()
    {
        using var fixture = new DatabaseFixture();
        fixture.Execute("""
            CREATE TABLE run_buildings AS SELECT 1 AS run_id, * FROM buildings;
            CREATE TABLE run_sub_areas AS SELECT 1 AS run_id, * FROM sub_areas;
            CREATE TABLE run_pages AS SELECT 1 AS run_id, id, sub_area_id AS run_sub_area_id, page_name, layout, collected_at FROM pages;
            CREATE TABLE run_cards AS SELECT 1 AS run_id, id, page_id AS run_page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm FROM cards;
            CREATE TABLE run_area_group_rules(id INTEGER,run_id INTEGER,group_id INTEGER,rule_order INTEGER,building TEXT,zuo TEXT,floor_label TEXT,floor_value REAL,match_mode TEXT,keywords TEXT,note TEXT,group_name TEXT,enabled INTEGER);
            INSERT INTO run_area_group_rules VALUES(1,1,11,1,'1号','','',NULL,'include','GQ','','历史同名',1),(2,1,12,1,'1号','','',NULL,'include','ROOM','','历史同名',1);
            CREATE TABLE monitor_groups(id INTEGER,name TEXT,enabled INTEGER,group_key TEXT DEFAULT 'test');
            CREATE TABLE area_group_rules(id INTEGER,group_id INTEGER,rule_order INTEGER,building TEXT,zuo TEXT,floor_label TEXT,floor_value REAL,match_mode TEXT,keywords TEXT,note TEXT);
            INSERT INTO monitor_groups(id,name,enabled) VALUES(11,'当前改名',1),(12,'当前禁用',0);
            INSERT INTO area_group_rules VALUES(1,11,1,'1号','','',NULL,'include','','');
            """);
        using var repository = new SqliteDeviceReadRepository(fixture.Path);
        foreach (var mutation in new[] { "SELECT 1", "UPDATE monitor_groups SET enabled=0", "DELETE FROM monitor_groups" })
        {
            fixture.Execute(mutation);
            var historical = await repository.LoadFilterOptionsAsync(new DeviceQuery(RunId: 1));
            Assert.Equal(new[] { new DeviceAreaGroupOption(11, "历史同名", 1), new DeviceAreaGroupOption(12, "历史同名", 1) }, historical.AreaGroups);
            Assert.Equal("GQ", Assert.Single((await repository.SearchAsync(new DeviceQuery(RunId: 1, MonitorGroupIds: "11"))).Rows).Name);
            var overview = await new EmsScout.Application.DashboardOverviewService(repository,
                new SqliteAreaGroupRepository(() => fixture.Path)).LoadAsync(1);
            Assert.Equal(new[] { 11L, 12L }, overview.AreaGroups.Select(group => group.Id));
            Assert.All(overview.AreaGroups, group => { Assert.Equal("历史同名", group.Name); Assert.Equal(1, group.Total); });
        }
        Assert.Empty((await repository.LoadFilterOptionsAsync()).AreaGroups!);
    }

    [Fact]
    public async Task ExactOwnerCannotBeStolenByAnEarlierOrFilteredSameNameDevice()
    {
        using var fixture = new DatabaseFixture();
        fixture.Execute("UPDATE pages SET page_name='一页'; INSERT INTO pages VALUES(2,1,'二页','grid','2026-09-22'); UPDATE cards SET name='SAME'; UPDATE cards SET page_id=2 WHERE id=2;");
        var detail = new RealtimeDetailRecord("owner", "test", DateTimeOffset.UtcNow, "1号", 1, "1F", "二页", "SAME", "dev", "", "", 0, 0, 0, false, "", "关机", "OFF", "",
            new Dictionary<string, string> { ["集控锁定"] = "开启" }, new Dictionary<string, bool> { ["集控锁定"] = true });
        using var repository = new SqliteDeviceReadRepository(fixture.Path, new CountingSource { Rows = [detail] });
        foreach (var query in new[] { new DeviceQuery(), new DeviceQuery(PageName: "一页"), new DeviceQuery(PageName: "二页"),
            new DeviceQuery(CommunicationState: "开机"), new DeviceQuery(CommunicationState: "关机"),
            new DeviceQuery(SortBy: "name", SortDescending: true), new DeviceQuery(Limit: 1), new DeviceQuery(Limit: 1, Offset: 1) })
        {
            var result = await repository.SearchAsync(query);
            Assert.NotEmpty(result.Rows);
            foreach (var row in result.Rows)
            {
                if (row.Id == 1) Assert.Null(row.Realtime);
                else { Assert.Equal("exact", row.RealtimeMatchKind); Assert.Equal("开启", row.RealtimeLockText); }
            }
        }
        var locks = await repository.SearchWithFilterOptionsAsync(new DeviceQuery(RealtimeLock: "开启"));
        Assert.Equal(2, Assert.Single(locks.Page.Rows).Id);
        Assert.Equal(1, Assert.Single(locks.FilterOptions.RealtimeLocks!, option => option.Value == "开启").Count);
        var exported = await new SqliteDeviceExportService(repository).ExportAsync(new DeviceQuery(),
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "export"));
        var cells = UserDeviceWorkbookAssert.ReadRows(exported.Path).Skip(1).ToArray();
        Assert.Equal("无实时数据", Assert.Single(cells, row => row[3] == "第1页")[11]);
        Assert.Equal("开启", Assert.Single(cells, row => row[3] == "第2页")[11]);
    }

    [Fact]
    public async Task SingletonNameFallbackSurvivesButAmbiguousDatabaseNameDoesNot()
    {
        using var fixture = new DatabaseFixture();
        var detail = new RealtimeDetailRecord("fallback", "test", DateTimeOffset.UtcNow, "1号", 99, "other", "9", "GQ", "dev", "", "", 0, 0, 0, false, "", "关机", "OFF", "", new Dictionary<string, string>(), new Dictionary<string, bool>());
        using var repository = new SqliteDeviceReadRepository(fixture.Path, new CountingSource { Rows = [detail] });
        Assert.Equal("name", (await repository.SearchAsync(new DeviceQuery(DeviceName: "GQ"))).Rows.Single().RealtimeMatchKind);
        fixture.Execute("UPDATE cards SET name='GQ' WHERE id=2");
        Assert.All((await repository.SearchAsync(new DeviceQuery(CommunicationState: "开机"))).Rows, row => Assert.Null(row.Realtime));
    }

    [Fact]
    public async Task LockedRealtimeFileIsRetriedAfterUnlockWithoutChangingItsMetadata()
    {
        using var fixture = new DatabaseFixture();
        var directory = System.IO.Path.GetDirectoryName(fixture.Path)!;
        var jsonPath = System.IO.Path.Combine(directory, "realtime_1号_latest.json");
        await File.WriteAllTextAsync(jsonPath, """
            {"runId":1,"capturedAt":"2026-09-22T00:00:00Z","rows":[{"building":"1号","name":"GQ","fields":{"集控锁定":"开启"}}]}
            """);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "realtime_1号_batch_old.json"), """
            {"runId":2,"batchUid":"older","runKey":"older","capturedAt":"2020-09-22T00:00:00Z","rows":[]}
            """);
        var source = new EmsScout.Infrastructure.Realtime.RealtimeLatestJsonSource(directory, directory);
        using var repository = new SqliteDeviceReadRepository(fixture.Path, source);
        var revision = await source.GetRevisionAsync();
        using (var locked = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = await repository.SearchAsync(new DeviceQuery());
            Assert.False(failed.IsCacheable);
            Assert.All(failed.Rows, row => Assert.Null(row.Realtime));
        }
        Assert.Equal(revision, await source.GetRevisionAsync());
        var recovered = await repository.SearchAsync(new DeviceQuery());
        Assert.True(recovered.IsCacheable);
        Assert.NotNull(recovered.Rows.Single(row => row.Name == "GQ").Realtime);
    }

    [Fact]
    public async Task TransientRealtimeFailureRecoversWithoutFileRevisionChange()
    {
        using var fixture = new DatabaseFixture();
        var source = new CountingSource
        {
            ResultFactory = _ => new RealtimeDetailSet([], RealtimeDetailAvailability.Unavailable,
                "temporarily locked", isTransientFailure: true)
        };
        using var repository = new SqliteDeviceReadRepository(fixture.Path, source);
        var degraded = await repository.SearchAsync(new DeviceQuery());
        Assert.False(degraded.IsCacheable);
        source.ResultFactory = _ => new RealtimeDetailSet([]);
        var recovered = await repository.SearchAsync(new DeviceQuery());
        Assert.True(recovered.IsCacheable);
        Assert.Empty(recovered.DataStatusText);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task RealtimeFileRevisionTracksCreateEditDeleteAndDirectorySwitch()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ems-realtime-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var selectedDirectory = directory;
        var source = new EmsScout.Infrastructure.Realtime.RealtimeLatestJsonSource(directory, () => selectedDirectory);
        try
        {
            var empty = await source.GetRevisionAsync();
            var path = System.IO.Path.Combine(directory, "realtime_1号_latest.json");
            await File.WriteAllTextAsync(path, "{}");
            var created = await source.GetRevisionAsync();
            Assert.NotEqual(empty, created);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
            Assert.NotEqual(created, await source.GetRevisionAsync());
            File.Delete(path);
            Assert.Equal(empty, await source.GetRevisionAsync());
            selectedDirectory = System.IO.Path.Combine(directory, "other");
            Directory.CreateDirectory(selectedDirectory);
            Assert.NotEqual(empty, await source.GetRevisionAsync());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PagingAndFacetsReusePreparedDataUntilAnExternalCommit()
    {
        using var fixture = new DatabaseFixture();
        var source = new CountingSource();
        using var repository = new SqliteDeviceReadRepository(fixture.Path, source);
        var first = await repository.SearchWithFilterOptionsAsync(new DeviceQuery(Limit: 1));
        var second = await repository.SearchAsync(new DeviceQuery(Limit: 1, Offset: 1));
        await repository.LoadFilterOptionsAsync(new DeviceQuery(CommunicationState: "开机"));
        var repeated = await repository.SearchWithFilterOptionsAsync(new DeviceQuery(Limit: 500));
        Assert.Same(first.FilterOptions, repeated.FilterOptions);
        Assert.Equal(2, first.Page.Total);
        Assert.Equal("ROOM", Assert.Single(second.Rows).Name);
        Assert.Equal(1, source.Calls);

        fixture.Execute("UPDATE cards SET comm = '离线' WHERE id = 2");
        var changed = await repository.SearchAsync(new DeviceQuery(CommunicationState: "离线"));
        Assert.Equal("ROOM", Assert.Single(changed.Rows).Name);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task SameVersionConcurrentReadersSharePreparationAndSourceRevisionInvalidatesIt()
    {
        using var fixture = new DatabaseFixture();
        var source = new CountingSource();
        using var repository = new SqliteDeviceReadRepository(fixture.Path, source);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => repository.SearchAsync(new DeviceQuery(Limit: 1))));
        Assert.Equal(1, source.Calls);
        source.Revision = "2";
        await repository.SearchAsync(new DeviceQuery(Limit: 1));
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task CombinedQueryPreservesSqlCaseAndLiteralNameBehavior()
    {
        using var fixture = new DatabaseFixture();
        fixture.Execute("UPDATE cards SET name = 'A_%', mode = 'Cool' WHERE id = 1");
        using var repository = new SqliteDeviceReadRepository(fixture.Path);
        var wrongCase = await repository.SearchWithFilterOptionsAsync(new DeviceQuery(Mode: "cool"));
        Assert.Equal(0, wrongCase.Page.Total);
        var literal = await repository.SearchWithFilterOptionsAsync(new DeviceQuery(DeviceName: "_%"));
        Assert.Equal("A_%", Assert.Single(literal.Page.Rows).Name);
    }

    [Fact]
    public async Task RulePreviewPreservesManualSeatCorrections()
    {
        using var fixture = new DatabaseFixture();
        fixture.Execute("""
            UPDATE buildings SET building='6号'; UPDATE sub_areas SET building='6号';
            CREATE TABLE realtime_match_overrides(id INTEGER PRIMARY KEY,building TEXT,dev_id TEXT,floor_label TEXT,sub_area TEXT,page_name TEXT,realtime_name TEXT,action TEXT,target_card_id INTEGER,zuo_override TEXT,area_type_override TEXT,note TEXT);
            INSERT INTO realtime_match_overrides VALUES(1,'6号','dev1','1F','1F','1','GQ','map_to_db',1,'B座','','');
            """);
        var detail = new RealtimeDetailRecord("row1", "test", DateTimeOffset.UtcNow, "6号", 1, "1F", "1", "GQ", "dev1", "", "", 0, 0, 0, false, "", "开机", "ON", "", new Dictionary<string,string>(), new Dictionary<string,bool>());
        using var repository = new SqliteDeviceReadRepository(fixture.Path, new CountingSource { Rows = [detail] });
        var rule = new EmsScout.Application.Groups.AreaGroupRuleRecord(1, 1, 1, "6号", "B座", "1F", 1, "include", ["GQ"], "");
        var page = await repository.SearchAsync(new DeviceQuery());
        Assert.Equal("B座", page.Rows.Single(row => row.Name == "GQ").Zuo);
        Assert.Equal(1, Assert.Single(await repository.CountAreaGroupRuleMatchesAsync([rule])));
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelSharedPreparation()
    {
        using var fixture = new DatabaseFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new CountingSource { BeforeLoad = async () => { started.TrySetResult(); await release.Task; } };
        using var repository = new SqliteDeviceReadRepository(fixture.Path, source);
        using var cancellation = new CancellationTokenSource();
        var first = repository.SearchAsync(new DeviceQuery(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = repository.SearchAsync(new DeviceQuery());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.Equal(2, (await second).Total);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task FailedPreparationCanRetryAndMidReadCommitCannotPublishOldRows()
    {
        using var fixture = new DatabaseFixture();
        var fail = true;
        var source = new CountingSource { BeforeLoad = () =>
        {
            if (fail) { fail = false; throw new IOException("transient"); }
            return Task.CompletedTask;
        }};
        using var repository = new SqliteDeviceReadRepository(fixture.Path, source);
        await Assert.ThrowsAsync<IOException>(() => repository.SearchAsync(new DeviceQuery()));
        var changed = false;
        source.BeforeLoad = () =>
        {
            if (!changed) { changed = true; fixture.Execute("UPDATE cards SET comm='离线' WHERE id=2"); }
            return Task.CompletedTask;
        };
        var result = await repository.SearchAsync(new DeviceQuery());
        Assert.Equal("ROOM", Assert.Single(result.Rows, row => row.CommunicationText == "离线").Name);
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task NarrowBuildingReadKeepsItsOwnRealtimeProvenance()
    {
        using var fixture = new DatabaseFixture();
        fixture.Execute("INSERT INTO sub_areas VALUES(2,'2号',1,'1F',1,100,100); INSERT INTO pages VALUES(2,2,'1','grid','2026-09-22'); UPDATE cards SET page_id=2 WHERE id=2;");
        var source = new CountingSource { ResultFactory = buildings => new RealtimeDetailSet([], RealtimeDetailAvailability.MissingSnapshot, string.Join(",", buildings.Order())) };
        using var repository = new SqliteDeviceReadRepository(fixture.Path, source);
        var broad = await repository.SearchWithFilterOptionsAsync(new DeviceQuery());
        Assert.Equal("1号,2号", broad.Page.DataStatusText);
        var narrow = await repository.SearchWithFilterOptionsAsync(new DeviceQuery(Building: "1号"));
        Assert.Equal("1号", narrow.Page.DataStatusText);
        Assert.Equal("GQ", Assert.Single(narrow.Page.Rows).Name);
    }

    private sealed class CountingSource : IRealtimeDetailSource, IDeviceReadRevisionSource
    {
        public int Calls;
        public Func<Task>? BeforeLoad;
        public Func<IReadOnlyList<string>, RealtimeDetailSet>? ResultFactory;
        public IReadOnlyList<RealtimeDetailRecord> Rows = [];
        public string Revision = "1";
        public Task<string> GetRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Revision);
        public async Task<RealtimeDetailSet> LoadAsync(IReadOnlyList<string> buildings, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (BeforeLoad is not null) await BeforeLoad();
            return ResultFactory?.Invoke(buildings) ?? new RealtimeDetailSet(Rows);
        }
        public Task<RealtimeDetailSet> LoadAsync(IReadOnlyList<string> buildings, long? expectedRunId, CancellationToken cancellationToken = default) => LoadAsync(buildings, cancellationToken);
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ems-perf-tests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "test.db");
        public DatabaseFixture()
        {
            Directory.CreateDirectory(_directory);
            Execute("""
                PRAGMA journal_mode=WAL;
                CREATE TABLE buildings(building TEXT, updated_at TEXT);
                CREATE TABLE sub_areas(id INTEGER PRIMARY KEY,building TEXT,floor REAL,text TEXT,sub_idx INTEGER,x REAL,y REAL);
                CREATE TABLE pages(id INTEGER PRIMARY KEY,sub_area_id INTEGER,page_name TEXT,layout TEXT,collected_at TEXT);
                CREATE TABLE cards(id INTEGER PRIMARY KEY,page_id INTEGER,name TEXT,switch TEXT,mode TEXT,indoor TEXT,set_temp TEXT,fan TEXT,indicator TEXT,comm TEXT);
                CREATE TABLE collection_runs(id INTEGER PRIMARY KEY,completed_at TEXT);
                INSERT INTO collection_runs VALUES(1,'2026-09-22T00:00:00Z');
                INSERT INTO buildings VALUES('1号','2026-09-22T00:00:00Z');
                INSERT INTO sub_areas VALUES(1,'1号',1,'1F',1,100,100);
                INSERT INTO pages VALUES(1,1,'1','grid','2026-09-22T00:00:00Z');
                INSERT INTO cards VALUES(1,1,'GQ','ON','制冷','26','24','中','','开机'),(2,1,'ROOM','OFF','制冷','26','24','中','','关机');
                """);
        }
        public void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
    }
}
