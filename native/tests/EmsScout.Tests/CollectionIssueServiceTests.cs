using System.Text.Json;
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

        Assert.Empty(report.Records);
        Assert.NotEmpty(report.Warnings);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-collection-issues", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
