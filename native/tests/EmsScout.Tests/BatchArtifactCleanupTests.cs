using EmsScout.Infrastructure.Sqlite;

namespace EmsScout.Tests;

public sealed class BatchArtifactCleanupTests
{
    [Fact]
    public async Task FindsAndCleansBatchReportsJsonNdjsonWithoutTouchingSharedLatestFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "ems-scout-batch-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "ac.db");
        File.WriteAllBytes(databasePath, []);
        var run = new EmsScout.Application.Collection.CollectionRunRecord(
            1,
            "run-1",
            "2026-09-21T00:00:00Z",
            "2026-09-21T00:01:00Z",
            "2026-09-21T00:01:00Z",
            "completed",
            "partial",
            ["1号"],
            "",
            "",
            1,
            0,
            1,
            0,
            0,
            "",
            false,
            "",
            1,
            "采集导入",
            "v1.0.0",
            "本机",
            null,
            "batch-1");

        var identity = $"{{\"runId\":{run.Id},\"batchUid\":\"{run.BatchUid}\",\"runKey\":\"{run.RunKey}\"}}";
        File.WriteAllText(Path.Combine(root, $"quality_report_run{run.Id}.json"), identity);
        File.WriteAllText(Path.Combine(root, $"quality_report_run{run.Id}.txt"), "report");
        File.WriteAllText(Path.Combine(root, $"realtime_all_buildings_batch_summary_run{run.Id}.json"), identity);
        File.WriteAllText(Path.Combine(root, $"realtime_all_buildings_batch_failure_run{run.Id}.json"), identity);
        File.WriteAllText(Path.Combine(root, $"realtime_quality_classified_{run.Id}.json"), identity);
        File.WriteAllText(Path.Combine(root, "realtime_1号.ndjson"), identity);
        File.WriteAllText(Path.Combine(root, "realtime_all_batch_20260921_000000.log"), "log");
        File.WriteAllText(Path.Combine(root, "collection_manifest_1.json"),
            $"{{\"runId\":{run.Id},\"batchUid\":\"{run.BatchUid}\",\"runKey\":\"{run.RunKey}\",\"resultFiles\":[\"realtime_1号.ndjson\",\"realtime_all_batch_20260921_000000.log\"]}}");
        File.WriteAllText(Path.Combine(root, "quality_report.json"), "shared");
        File.WriteAllText(Path.Combine(root, "realtime_all_buildings_latest.json"), "shared");

        var cleaner = new CollectionRunArtifactCleaner(() => databasePath);
        var candidates = cleaner.FindCandidates(run);
        var cleanup = cleaner.Cleanup(run, candidates);

        Assert.Contains(candidates, item => item.RelativePath == $"realtime_quality_classified_{run.Id}.json" && item.IdentityVerified);
        Assert.Contains(candidates, item => item.RelativePath == "realtime_1号.ndjson" && item.IdentityVerified);
        Assert.True(cleanup.IsComplete);
        Assert.False(File.Exists(Path.Combine(root, $"quality_report_run{run.Id}.json")));
        Assert.False(File.Exists(Path.Combine(root, $"realtime_quality_classified_{run.Id}.json")));
        Assert.False(File.Exists(Path.Combine(root, "realtime_1号.ndjson")));
        Assert.True(File.Exists(Path.Combine(root, "quality_report.json")));
        Assert.True(File.Exists(Path.Combine(root, "realtime_all_buildings_latest.json")));
    }
}
