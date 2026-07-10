using EmsScout.Infrastructure.Quality;
using System.Text.Json;

namespace EmsScout.Tests;

public sealed class QualityAuditServiceTests
{
    [Fact]
    public async Task LoadsQualityReportAndMarksStaleWhenDatabaseIsNewer()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-quality-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "ac.db");
        var reportPath = Path.Combine(root, "quality_report.json");
        File.WriteAllText(dbPath, string.Empty);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            generated_at = "2026-07-01T10:51:56.564Z",
            generated_at_local = "2026-07-01 18:51:56",
            run_id = (long?)null,
            summary = new
            {
                total_cards = 6568,
                issue_count = 2,
                placeholder_cards = 0,
                state_mismatch = 0,
                unknown_comm = 3,
                missing_indicator = 3,
                unknown_switch = 0,
                duplicate_cards_same_page = 0,
                duplicate_rendered_pages = 1,
                empty_sub_areas = 0,
                inline_sub_areas = 1,
                suspicious_uniform_pages = 0,
                uniform_resolved_pages = 6,
            },
            issues = new[]
            {
                new { severity = "P2", code = "unknown_comm", count = 3, message = "存在未知通讯状态。" },
            },
        }));
        File.SetLastWriteTimeUtc(reportPath, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(dbPath, DateTime.UtcNow);
        var service = new JsonQualityAuditService(() => root, () => dbPath);

        var report = await service.LoadLatestAsync();

        Assert.NotNull(report);
        Assert.Equal(6568, report.Summary.TotalCards);
        Assert.Equal(2, report.Summary.IssueCount);
        Assert.Equal(3, report.Summary.UnknownCommunication);
        Assert.Single(report.Issues);
        Assert.True(report.IsStale);
    }

    [Fact]
    public async Task LoadsLatestRealtimeQualityAuditReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-realtime-quality-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldPath = Path.Combine(root, "realtime_quality_classified_20260701_010101.json");
        var latestPath = Path.Combine(root, "realtime_quality_classified_20260702_010101.json");
        File.WriteAllText(oldPath, "{}");
        File.WriteAllText(latestPath, JsonSerializer.Serialize(new
        {
            createdAt = "2026-07-06T04:04:41.910Z",
            input = new
            {
                summaryFile = "out/realtime_all_buildings_batch_summary_20260703_111935.json",
            },
            totalRows = 6575,
            uniqueDevices = 6575,
            collectionErrors = new
            {
                count = 0,
                byCategory = new { },
                rows = Array.Empty<object>(),
            },
            deviceAnomalies = new
            {
                rowCount = 1592,
                eventCount = 3523,
                byCategory = new
                {
                    invalidRealtimeTags = 1544,
                    invalidEnum = 470,
                    outOfRange = 466,
                    invalidLock = 15,
                },
                rows = Array.Empty<object>(),
            },
            byBuilding = new Dictionary<string, object>
            {
                ["1号"] = new
                {
                    rows = 1493,
                    collectionErrors = 0,
                    deviceAnomalyRows = 114,
                    deviceAnomalyEvents = 393,
                    deviceAnomalyCategories = new
                    {
                        invalidRealtimeTags = 111,
                        invalidEnum = 62,
                        outOfRange = 62,
                        invalidLock = 3,
                    },
                },
            },
            conclusion = new
            {
                collectionOk = true,
                note = "设备异常只记录，不作为采集失败。",
            },
        }));
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(latestPath, DateTime.UtcNow);
        var service = new JsonRealtimeQualityAuditService(() => root);

        var report = await service.LoadLatestAsync();

        Assert.NotNull(report);
        Assert.Equal(6575, report.TotalRows);
        Assert.True(report.CollectionOk);
        Assert.Equal(1592, report.DeviceAnomalyRows);
        Assert.Contains(report.DeviceAnomalyCategories, category => category.Code == "invalidRealtimeTags" && category.Label == "有效点位为 0");
        var building = Assert.Single(report.Buildings);
        Assert.Equal("1号", building.Building);
        Assert.Equal(114, building.DeviceAnomalyRows);
        Assert.Equal(3, building.InvalidLock);
    }
}
