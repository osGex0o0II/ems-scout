using System.Security.Cryptography;
using System.Text.Json;
using EmsScout.Application.Quality;
using EmsScout.Infrastructure.Quality;

namespace EmsScout.Tests;

public sealed class Run17GoldenParityTests
{
    [Fact]
    public async Task NativeAuditOfPrivateRun17CopyMatchesGoldenManifest()
    {
        var root = FindRepositoryRoot();
        var sourceDatabase = Path.Combine(root, "out", "ac.db");
        if (!File.Exists(sourceDatabase))
        {
            return;
        }

        var golden = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            root,
            "tests",
            "fixtures",
            "run17",
            "golden-v1.json"))).RootElement.Clone();
        var expectedHash = golden.GetProperty("sources").GetProperty("databaseSha256").GetString();
        var sourceHashBefore = await Sha256Async(sourceDatabase);
        Assert.Equal(expectedHash, sourceHashBefore);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ems-run17-golden-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var shadowDatabase = Path.Combine(temporaryRoot, "ac.db");
        File.Copy(sourceDatabase, shadowDatabase);

        try
        {
            var service = new SqliteQualityAuditService(
                () => shadowDatabase,
                () => Path.Combine(root, "config", "quality-known-findings.json"));
            var report = await service.AuditAsync(NativeQualityAuditRequest.LatestCompletedRun);
            Assert.NotNull(report);
            var expected = golden.GetProperty("nativeAudit");

            Assert.Equal(golden.GetProperty("runId").GetInt64(), report.RunId);
            Assert.Equal(expected.GetProperty("totalCards").GetInt32(), report.Summary.TotalCards);
            Assert.Equal(expected.GetProperty("issueCount").GetInt32(), report.Summary.IssueCount);
            Assert.Equal(expected.GetProperty("knownFindings").GetInt32(), report.Summary.KnownFindings);
            Assert.Equal(expected.GetProperty("blockingKnownFindings").GetInt32(), report.Summary.BlockingKnownFindings);
            Assert.Equal(expected.GetProperty("nonBlockingKnownFindings").GetInt32(), report.Summary.NonBlockingKnownFindings);
            Assert.Equal(expected.GetProperty("unknownCommunication").GetInt32(), report.Summary.UnknownCommunication);
            Assert.Equal(expected.GetProperty("missingIndicator").GetInt32(), report.Summary.MissingIndicator);
            Assert.Equal(expected.GetProperty("duplicateRenderedPages").GetInt32(), report.Summary.DuplicateRenderedPages);
            Assert.Equal(expected.GetProperty("inlineSubAreas").GetInt32(), report.Summary.InlineSubAreas);
            Assert.Equal(expected.GetProperty("uniformResolvedPages").GetInt32(), report.Summary.UniformResolvedPages);
            Assert.Equal(expected.GetProperty("detectedOfflineTemplateStable").GetInt32(), report.Summary.DetectedOfflineTemplateStable);
            Assert.Equal(expected.GetProperty("offlineTemplateStable").GetInt32(), report.Summary.OfflineTemplateStable);
            Assert.Equal(expected.GetProperty("detectedInvalidCardFields").GetInt32(), report.Summary.DetectedInvalidCardFields);
            Assert.Equal(expected.GetProperty("invalidCardFields").GetInt32(), report.Summary.InvalidCardFields);
            Assert.Equal(expected.GetProperty("detectedActiveFieldIncompletePages").GetInt32(), report.Summary.DetectedActiveFieldIncompletePages);
            Assert.Equal(expected.GetProperty("activeFieldIncompletePages").GetInt32(), report.Summary.ActiveFieldIncompletePages);

            var actualBlockingIssues = report.Issues
                .Where(issue => issue.Severity != "INFO")
                .ToDictionary(issue => issue.Code, issue => issue.Count, StringComparer.Ordinal);
            var expectedBlockingIssues = expected.GetProperty("issues")
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetInt32(), StringComparer.Ordinal);
            Assert.Equal(expectedBlockingIssues, actualBlockingIssues);
            Assert.Equal(sourceHashBefore, await Sha256Async(sourceDatabase));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json"))) return current.FullName;
        }

        throw new DirectoryNotFoundException("Cannot locate EMS repository root.");
    }
}
