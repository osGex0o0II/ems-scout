using System.Text.Json;
using Microsoft.Data.Sqlite;
using EmsScout.Infrastructure.Quality;

namespace EmsScout.Tests;

public sealed class CollectionIssueServiceTests
{
    [Fact]
    public async Task LoadsAllQualityAndRealtimeIssueDetailsAndGroupsCompleteCounts()
    {
        var root = CreateTempRoot();
        var qualityRows = Enumerable.Range(1, 75)
            .Select(index => new
            {
                building = "1号",
                floor = "1F",
                page_name = "一页",
                name = $"1-{index:000}-KT",
                issue_code = "invalid_card_fields",
                severity = "P1",
                message = "字段不完整",
                evidence = "室内温度缺失",
            })
            .ToArray();

        await File.WriteAllTextAsync(
            Path.Combine(root, "quality_report_run24.json"),
            JsonSerializer.Serialize(new
            {
                run_id = 24,
                batch_uid = "batch-24",
                run_key = "run-24",
                details = new
                {
                    invalid_card_fields = qualityRows.Concat([qualityRows[0]]).ToArray(),
                },
            }));
        await File.WriteAllTextAsync(
            Path.Combine(root, "realtime_quality_classified_24.json"),
            JsonSerializer.Serialize(new
            {
                runId = 24,
                batchUid = "batch-24",
                runKey = "run-24",
                details = new
                {
                    realtime_collection_errors = new[]
                    {
                        new { category = "timeout", building = "2号", pageName = "2F", message = "等待超时" },
                    },
                    realtime_device_anomalies = new[]
                    {
                        new { building = "2号", device_name = "2-201-KT", issues = new[] { "集控锁定枚举异常" } },
                    },
                },
            }));

        var report = await new JsonCollectionIssueService(() => root, () => Path.Combine(root, "ac.db"))
            .LoadForRunAsync(24);

        Assert.Equal(77, report.Records.Count);
        Assert.Equal(75, report.Categories.Single(category => category.Code == "invalid_card_fields").Count);
        Assert.Equal(1, report.Categories.Single(category => category.Code == "realtime_timeout").Count);
        Assert.Equal(1, report.Categories.Single(category => category.Code == "realtime_device_anomaly").Count);
        Assert.Contains(report.Records, record => record.DeviceName == "2-201-KT");
        Assert.All(report.Records, record => Assert.Equal(24, record.BatchId));
    }

    [Fact]
    public async Task ReturnsWarningInsteadOfThrowingWhenAReportIsMalformed()
    {
        var root = CreateTempRoot();
        await File.WriteAllTextAsync(Path.Combine(root, "quality_report_run25.json"), "{broken");

        var report = await new JsonCollectionIssueService(() => root, () => Path.Combine(root, "ac.db"))
            .LoadForRunAsync(25);

        Assert.Contains(report.Records, record => record.IssueType == "report_parse_failure");
        Assert.Contains(report.Records, record =>
            record.IssueType == "report_parse_failure" &&
            Path.GetFileName(record.SourcePath) == "quality_report_run25.json");
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public async Task RejectsReportWhenBatchIdentityDoesNotMatchTheSelectedRun()
    {
        var root = CreateTempRoot();
        var databasePath = Path.Combine(root, "ac.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE collection_runs (id INTEGER PRIMARY KEY, batch_uid TEXT, run_key TEXT); INSERT INTO collection_runs (id, batch_uid, run_key) VALUES (26, 'batch-26', 'run-26');";
            await command.ExecuteNonQueryAsync();
        }

        await File.WriteAllTextAsync(
            Path.Combine(root, "quality_report_run26.json"),
            "{\"run_id\":26,\"batch_uid\":\"stale-batch\",\"run_key\":\"stale-run\",\"details\":{\"invalid_card_fields\":[{\"name\":\"stale\"}]}}");

        var report = await new JsonCollectionIssueService(() => root, () => databasePath)
            .LoadForRunAsync(26);

        Assert.Contains(report.Records, record => record.IssueType == "report_identity_mismatch");
        Assert.DoesNotContain(report.Records, record => record.IssueType == "invalid_card_fields");
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public async Task MissingReportsAreVisibleAsIssueRecords()
    {
        var root = CreateTempRoot();

        var report = await new JsonCollectionIssueService(() => root, () => Path.Combine(root, "ac.db"))
            .LoadForRunAsync(27);

        Assert.Contains(report.Records, record => record.IssueType == "report_missing" && record.SourceArtifact == "quality");
        Assert.Contains(report.Records, record => record.IssueType == "report_missing" && record.SourceArtifact == "realtime");
        Assert.Contains(report.Categories, category => category.Code == "report_missing");
    }

    [Fact]
    public async Task LegacyQualityIssuesAndSamplesAreNotHiddenWhenDetailsAreAbsent()
    {
        var root = CreateTempRoot();
        await File.WriteAllTextAsync(
            Path.Combine(root, "quality_report_run28.json"),
            JsonSerializer.Serialize(new
            {
                run_id = 28,
                issues = new[] { new { code = "state_mismatch", severity = "P1", count = 2, message = "状态不一致" } },
                samples = new
                {
                    inconsistent_state = new[] { new { building = "1号", floor = "1F", name = "1-0101-KT", reason = "通讯状态不一致" } },
                },
            }));

        var report = await new JsonCollectionIssueService(() => root, () => Path.Combine(root, "ac.db"))
            .LoadForRunAsync(28);

        Assert.Contains(report.Records, record => record.IssueType == "state_mismatch");
        Assert.Contains(report.Records, record => record.DeviceName == "1-0101-KT");
    }

    [Fact]
    public async Task LegacyRunIdOnlyReportRemainsInspectable()
    {
        var root = CreateTempRoot();
        var databasePath = Path.Combine(root, "ac.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE collection_runs (id INTEGER PRIMARY KEY, batch_uid TEXT, run_key TEXT); INSERT INTO collection_runs VALUES (30, 'batch-30', 'run-30');";
            await command.ExecuteNonQueryAsync();
        }

        await File.WriteAllTextAsync(
            Path.Combine(root, "quality_report_run30.json"),
            "{\"run_id\":30,\"issues\":[{\"code\":\"state_mismatch\",\"severity\":\"P1\",\"count\":1,\"message\":\"状态不一致\"}]}" );

        var report = await new JsonCollectionIssueService(() => root, () => databasePath)
            .LoadForRunAsync(30);

        Assert.Contains(report.Records, record => record.IssueType == "state_mismatch");
        Assert.DoesNotContain(report.Records, record => record.IssueType == "report_identity_mismatch");
    }

    [Fact]
    public async Task RealtimeIssueCategoriesHaveStableLabels()
    {
        var root = CreateTempRoot();
        await File.WriteAllTextAsync(
            Path.Combine(root, "realtime_quality_classified_29.json"),
            JsonSerializer.Serialize(new
            {
                runId = 29,
                details = new
                {
                    realtime_collection_errors = new[]
                    {
                        new { category = "timeout", message = "超时" },
                        new { category = "invalidEnum", message = "枚举" },
                        new { category = "outOfRange", message = "范围" },
                        new { category = "invalidLock", message = "锁定" },
                    },
                },
            }));

        var report = await new JsonCollectionIssueService(() => root, () => Path.Combine(root, "ac.db"))
            .LoadForRunAsync(29);

        Assert.Contains(report.Categories, category => category.Code == "realtime_timeout" && category.Label == "实时采集超时");
        Assert.Contains(report.Categories, category => category.Code == "realtime_invalidEnum" && category.Label == "实时枚举异常");
        Assert.Contains(report.Categories, category => category.Code == "realtime_outOfRange" && category.Label == "实时数值越界");
        Assert.Contains(report.Categories, category => category.Code == "realtime_invalidLock" && category.Label == "实时集控锁异常");
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-collection-issues", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
