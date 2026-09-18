using EmsScout.Infrastructure.Sqlite;
using EmsScout.Infrastructure.Realtime;
using EmsScout.Application.Devices;
using EmsScout.Domain;

namespace EmsScout.Tests;

public sealed class RealtimeLatestJsonSourceTests
{
    private static readonly string[] Buildings = ["1号", "2号", "3号", "4号", "5号", "6号"];

    [Fact]
    public async Task UsesCaptureTimestampFromJsonInsteadOfFileWriteTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-source-tests", Guid.NewGuid().ToString("N"));
        var outDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outDirectory);
        var path = Path.Combine(outDirectory, "realtime_1号_latest.json");
        await File.WriteAllTextAsync(path, """
            {
              "runId": 1,
              "capturedAt": "2026-08-31T04:10:00.000Z",
              "rows": [{
                "building": "1号",
                "name": "1-0101-KT",
                "fields": { "集控锁定": "开启" }
              }]
            }
            """);
        File.SetLastWriteTimeUtc(path, DateTimeOffset.Parse("2026-08-31T01:00:00Z").UtcDateTime);

        var source = new RealtimeLatestJsonSource(root, outDirectory);

        var details = await source.LoadAsync(["1号"]);

        Assert.Equal(DateTimeOffset.Parse("2026-08-31T04:10:00.000Z"), details.Rows[0].SourceUpdatedAt);
    }

    [Fact]
    public async Task PreservesRawFieldValuesWhenARealtimeEnumIsUnmapped()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-source-tests", Guid.NewGuid().ToString("N"));
        var outDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outDirectory);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "realtime_1号_latest.json"), """
            {
              "runId": 1,
              "capturedAt": "2026-08-31T04:10:00+08:00",
              "rows": [{
                "building": "1号",
                "name": "1-0101-KT",
                "fields": { "集控锁定": "32896" },
                "rawFields": { "集控锁定": "32896" },
                "validFields": { "集控锁定": true }
              }]
            }
            """);

        var details = await new RealtimeLatestJsonSource(root, outDirectory).LoadAsync(["1号"], expectedRunId: 1);
        var row = Assert.Single(details.Rows);

        Assert.Equal("32896", row.LockState);
        Assert.Equal("32896", row.RawField("集控锁定"));
        Assert.Equal("32896", row.RawLockState);
        Assert.False(row.LockStateValid);
    }

    [Fact]
    public async Task InterpretsLegacyTimestampWithoutOffsetAsUtc()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-source-tests", Guid.NewGuid().ToString("N"));
        var outDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outDirectory);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "realtime_1号_latest.json"), """
            {
              "runId": 1,
              "capturedAt": "2026-08-31T03:00:00",
              "rows": [{ "building": "1号", "name": "1-0101-KT" }]
            }
            """);

        var source = new RealtimeLatestJsonSource(root, outDirectory);

        var details = await source.LoadAsync(["1号"]);

        Assert.Equal(DateTimeOffset.Parse("2026-08-31T03:00:00Z"), details.Rows[0].SourceUpdatedAt);
    }

    [Fact]
    public async Task RejectsRealtimeFileWithoutRunId()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-source-tests", Guid.NewGuid().ToString("N"));
        var outDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outDirectory);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "realtime_1号_latest.json"), """
            {
              "capturedAt": "2026-08-31T03:00:00+08:00",
              "rows": [{ "building": "1号", "name": "1-0101-KT" }]
            }
            """);

        var details = await new RealtimeLatestJsonSource(root, outDirectory).LoadAsync(["1号"], expectedRunId: 1);

        Assert.Equal(RealtimeDetailAvailability.MissingSnapshot, details.Availability);
        Assert.Empty(details.Rows);
        Assert.Contains("批次", details.StatusText);
    }

    [Fact]
    public async Task RejectsMissingAndEmptyBuildingFilesInsteadOfReturningAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-source-tests", Guid.NewGuid().ToString("N"));
        var outDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outDirectory);
        var source = new RealtimeLatestJsonSource(root, outDirectory);

        var missing = await source.LoadAsync(["1号"], expectedRunId: 1);

        await File.WriteAllTextAsync(Path.Combine(outDirectory, "realtime_1号_latest.json"), """
            {
              "runId": 1,
              "capturedAt": "2026-08-31T03:00:00+08:00",
              "rows": []
            }
            """);
        var empty = await source.LoadAsync(["1号"], expectedRunId: 1);

        Assert.Equal(RealtimeDetailAvailability.MissingSnapshot, missing.Availability);
        Assert.Equal(RealtimeDetailAvailability.MissingSnapshot, empty.Availability);
        Assert.Contains("1号", missing.StatusText);
        Assert.Contains("空", empty.StatusText);
    }

    [Fact]
    public async Task RejectsRowsThatClaimAnotherBuilding()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-source-tests", Guid.NewGuid().ToString("N"));
        var outDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outDirectory);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "realtime_1号_latest.json"), """
            {
              "runId": 1,
              "capturedAt": "2026-08-31T03:00:00+08:00",
              "rows": [{ "building": "2号", "name": "2-0101-KT" }]
            }
            """);

        var details = await new RealtimeLatestJsonSource(root, outDirectory).LoadAsync(["1号"], expectedRunId: 1);

        Assert.Equal(RealtimeDetailAvailability.Unavailable, details.Availability);
        Assert.Empty(details.Rows);
        Assert.Contains("楼栋", details.StatusText);
    }

    [Fact]
    public async Task ChoosesNewestValidTimestampFileWhenLatestAliasIsStale()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-source-tests", Guid.NewGuid().ToString("N"));
        var outDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outDirectory);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "realtime_1号_latest.json"), """
            {
              "runId": 1,
              "capturedAt": "2026-08-31T03:00:00+08:00",
              "rows": [{ "building": "1号", "name": "old" }]
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "realtime_1号_20260831_040000.json"), """
            {
              "runId": 2,
              "capturedAt": "2026-08-31T04:00:00+08:00",
              "rows": [{ "building": "1号", "name": "new" }]
            }
            """);

        var details = await new RealtimeLatestJsonSource(root, outDirectory).LoadAsync(["1号"], expectedRunId: 2);

        Assert.Equal(RealtimeDetailAvailability.Available, details.Availability);
        Assert.Equal(2, details.SourceRunId);
        Assert.Equal("new", Assert.Single(details.Rows).Name);
    }

    [Fact]
    public async Task SearchesOlderFileWhenNewestFileBelongsToAnotherBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-source-identity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "realtime_1号_20260917_010000.json"), """
                {
                  "runId": 7,
                  "batchUid": "target-batch",
                  "runKey": "target-run",
                  "capturedAt": "2026-09-17T01:00:00+08:00",
                  "rows": [{"building":"1号","name":"target"}]
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "realtime_1号_20260917_020000.json"), """
                {
                  "runId": 8,
                  "batchUid": "other-batch",
                  "runKey": "other-run",
                  "capturedAt": "2026-09-17T02:00:00+08:00",
                  "rows": [{"building":"1号","name":"other"}]
                }
                """);

            var source = new RealtimeLatestJsonSource(root, root);
            var details = await source.LoadAsync(["1号"], 7, "target-batch", "target-run");

            Assert.Equal(RealtimeDetailAvailability.Available, details.Availability);
            Assert.Equal("target", Assert.Single(details.Rows).Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsRealtimeFileFromAnotherRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-source-tests", Guid.NewGuid().ToString("N"));
        var outDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outDirectory);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "realtime_1号_latest.json"), """
            {
              "runId": 7,
              "capturedAt": "2026-08-31T04:10:00.000Z",
              "rows": [{ "building": "1号", "name": "1-0101-KT" }]
            }
            """);

        var source = new RealtimeLatestJsonSource(root, outDirectory);

        var details = await source.LoadAsync(["1号"], expectedRunId: 8);

        Assert.Equal(RealtimeDetailAvailability.MissingSnapshot, details.Availability);
        Assert.Empty(details.Rows);
        Assert.Contains("当前批次 #8", details.StatusText);
        Assert.Equal(7, details.SourceRunId);
    }

    [Fact]
    public async Task RejectsRealtimeFileWithMatchingRunIdButDifferentBatchUid()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-batch-identity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "realtime_1号_20260917_010000.json"), """
                {
                  "runId": 7,
                  "batchUid": "old-batch",
                  "capturedAt": "2026-09-17T01:00:00+08:00",
                  "rows": [{"building":"1号","name":"1-0101-KT"}]
                }
                """);

            IRealtimeDetailSource source = new RealtimeLatestJsonSource(root, root);
            var details = await source.LoadAsync(["1号"], "new-batch");

            Assert.Equal(RealtimeDetailAvailability.MissingSnapshot, details.Availability);
            Assert.Contains("批次", details.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsLegacyRealtimeFilesWithoutBatchMetadata()
    {
        var source = CurrentRealtimeSource();

        var details = await source.LoadAsync(Buildings, expectedRunId: 24);

        Assert.Equal(RealtimeDetailAvailability.MissingSnapshot, details.Availability);
        Assert.Empty(details.Rows);
    }

    [Fact]
    public async Task AttachesRealtimeDetailsToCurrentDatabaseRows()
    {
        var root = LocateRepositoryRoot();
        var repository = new SqliteDeviceReadRepository(
            Path.Combine(root, "out", "ac.db"),
            UnvalidatedCurrentRealtimeSource());

        var result = await repository.SearchAsync(new(Limit: 50000));

        Assert.True(result.Total > 0);
        Assert.True(result.Facets.RealtimeRows > 0);
        var onlineRows = result.Rows.Count(row => row.CommunicationState != DeviceCommunicationState.Offline);
        Assert.Equal(onlineRows, result.Facets.RealtimeMatched + result.Facets.RealtimeMissing);
        Assert.True(result.Facets.RealtimeUnmatched >= 0);
        Assert.Equal(result.Rows.Count(row => row.RealtimeLocked), result.Facets.RealtimeLocked);
        Assert.Equal(onlineRows, result.Facets.RealtimePointsComplete + result.Facets.RealtimePointsIncomplete);
        Assert.Equal(result.Rows.Count(row => row.CommunicationState != DeviceCommunicationState.Offline && row.Realtime?.IsInvalid == true), result.Facets.RealtimeInvalid);
        Assert.Equal(0, result.Facets.VirtualManaged);
        Assert.True(result.Facets.ManualOverrides > 0);
        Assert.Equal("已匹配", result.Rows[0].RealtimeMatchLabel);
        Assert.NotNull(result.Rows[0].Realtime);
    }

    [Fact]
    public async Task DoesNotAttachRealtimeRowsWhenCurrentFileDoesNotBelongToCurrentRun()
    {
        var root = LocateRepositoryRoot();
        var repository = new SqliteDeviceReadRepository(
            Path.Combine(root, "out", "ac.db"),
            CurrentRealtimeSource());

        var result = await repository.SearchAsync(new(Limit: 50000));

        Assert.Equal(0, result.Facets.RealtimeRows);
        Assert.Contains("批次", result.DataStatusText);
        Assert.All(result.Rows, row => Assert.Null(row.Realtime));
    }

    [Fact]
    public async Task AppliesRealtimeMatchOverridesAndVirtualManagedDevices()
    {
        var root = LocateRepositoryRoot();
        var repository = new SqliteDeviceReadRepository(
            Path.Combine(root, "out", "ac.db"),
            UnvalidatedCurrentRealtimeSource());

        var virtualDevice = await repository.SearchAsync(new(SearchText: "2F-HTDTT-KT-2", RealtimeMatch: "virtual", Limit: 5));
        var manualDevices = await repository.SearchAsync(new(RealtimeMatch: "manual", Limit: 5));

        Assert.Equal(1, virtualDevice.Total);
        Assert.Equal(-10, virtualDevice.Rows[0].Id);
        Assert.True(virtualDevice.Rows[0].IsVirtual);
        Assert.Equal("虚拟纳管", virtualDevice.Rows[0].RealtimeMatchLabel);
        Assert.Equal("create_virtual", virtualDevice.Rows[0].MatchOverrideAction);
        Assert.Equal("公区", virtualDevice.Rows[0].AreaType);
        Assert.Equal("20008942", virtualDevice.Rows[0].Realtime?.DevId);

        Assert.True(manualDevices.Total > 0);
        Assert.Contains(manualDevices.Rows, row =>
            !row.IsVirtual &&
            row.RealtimeMatchLabel == "手动匹配" &&
            row.MatchOverrideAction == "map_to_db" &&
            row.Realtime is not null);
        Assert.All(manualDevices.Rows, row =>
        {
            Assert.True(row.HasManualOverride);
        });
    }

    [Fact]
    public async Task AppliesNativeDataWorkbenchFilters()
    {
        var root = LocateRepositoryRoot();
        var repository = new SqliteDeviceReadRepository(
            Path.Combine(root, "out", "ac.db"),
            UnvalidatedCurrentRealtimeSource());

        Assert.Equal(10, (await repository.SearchAsync(new(Floor: "2.5F", Limit: 1))).Total);
        Assert.Equal(24, (await repository.SearchAsync(new(Floor: "B1F", Limit: 1))).Total);
        Assert.Equal(23, (await repository.SearchAsync(new(Building: "5号", Zuo: "A座", Limit: 1))).Total);
        Assert.Equal(889, (await repository.SearchAsync(new(Building: "6号", Zuo: "C座", Limit: 1))).Total);

        var matched = await repository.SearchAsync(new(RealtimeMatch: "matched", Limit: 1));
        var missing = await repository.SearchAsync(new(RealtimeMatch: "missing", Limit: 1));
        var all = await repository.SearchAsync(new(Limit: 50000));
        var onlineTotal = all.Rows.Count(row => row.CommunicationState != DeviceCommunicationState.Offline);
        Assert.Equal(onlineTotal, matched.Total + missing.Total);
        Assert.True((await repository.SearchAsync(new(RealtimeMatch: "invalid", Limit: 1))).Total > 0);
        Assert.True((await repository.SearchAsync(new(RealtimeMatch: "manual", Limit: 1))).Total > 0);
        Assert.Equal(2, (await repository.SearchAsync(new(RealtimeMatch: "virtual", Limit: 1))).Total);

        var completePoints = await repository.SearchAsync(new(RealtimePoints: "complete", Limit: 1));
        var incompletePoints = await repository.SearchAsync(new(RealtimePoints: "incomplete", Limit: 1));
        var missingPoints = await repository.SearchAsync(new(RealtimePoints: "missing", Limit: 1));
        Assert.Equal(onlineTotal, completePoints.Total + incompletePoints.Total);
        Assert.True(missingPoints.Total <= incompletePoints.Total);
        Assert.Equal(1, (await repository.SearchAsync(new(SearchText: "2F-HTDTT-KT-2", RealtimeMatch: "virtual", Limit: 1))).Total);
    }

    private static RealtimeLatestJsonSource CurrentRealtimeSource()
    {
        var root = LocateRepositoryRoot();
        return new RealtimeLatestJsonSource(root, Path.Combine(root, "out"));
    }

    private static EmsScout.Application.Devices.IRealtimeDetailSource UnvalidatedCurrentRealtimeSource()
    {
        var root = LocateRepositoryRoot();
        return new LegacyCurrentRealtimeSource(
            new RealtimeLatestJsonSource(root, Path.Combine(root, "out")));
    }

    private sealed class LegacyCurrentRealtimeSource(RealtimeLatestJsonSource source)
        : EmsScout.Application.Devices.IRealtimeDetailSource
    {
        public Task<RealtimeDetailSet> LoadAsync(
            IReadOnlyList<string> buildings,
            CancellationToken cancellationToken = default)
            => source.LoadAsync(buildings, cancellationToken);
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
