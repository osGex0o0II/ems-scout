using System.Security.Cryptography;
using System.Text.Json;
using EmsScout.Application.Quality;
using EmsScout.Infrastructure.Quality;
using Microsoft.Data.Sqlite;

namespace EmsScout.Tests;

public sealed class SqliteQualityAuditServiceTests
{
    [Fact]
    public async Task LoadLatestUsesLatestCompletedRunAndDoesNotModifyDatabase()
    {
        using var fixture = await QualityDatabaseFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            INSERT INTO sub_areas (id, building, floor, text) VALUES (1, 'CURRENT', 1, '1F');
            INSERT INTO pages (id, sub_area_id, page_name) VALUES (1, 1, 'default');
            INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (1, 1, 'CURRENT-ONLY', 'ON', '制冷', '24', '20', '高', 'red.png', '开机');

            INSERT INTO collection_runs (id, completed_at, status)
            VALUES (6, '2026-07-10T06:00:00Z', 'completed'),
                   (7, '2026-07-10T07:00:00Z', 'completed'),
                   (8, '2026-07-10T08:00:00Z', 'in_progress');

            INSERT INTO run_sub_areas (id, run_id, building, floor, text)
            VALUES (61, 6, 'OLD', 1, '1F'),
                   (71, 7, 'LATEST', 1, '1F'),
                   (81, 8, 'IN-PROGRESS', 1, '1F');
            INSERT INTO run_pages (id, run_id, run_sub_area_id, page_name)
            VALUES (61, 6, 61, 'default'),
                   (71, 7, 71, 'default'),
                   (81, 8, 81, 'default');
            INSERT INTO run_cards (id, run_id, run_page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (61, 6, 61, 'OLD', 'OFF', '制冷', '23', '20', '低', 'green.png', '关机'),
                   (71, 7, 71, 'LATEST-A', 'ON', '制冷', '24', '20', '高', 'red.png', '开机'),
                   (72, 7, 71, 'LATEST-B', 'OFF', '通风', '25', '21', '低', 'green.png', '关机'),
                   (81, 8, 81, '0-0001-KT', '-', '0', '0', '0', '0', '', '');
            """);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(fixture.DatabasePath));
        var service = new SqliteQualityAuditService(() => fixture.DatabasePath);

        var report = await service.LoadLatestAsync();

        var after = SHA256.HashData(await File.ReadAllBytesAsync(fixture.DatabasePath));
        Assert.NotNull(report);
        Assert.Equal(7, report.RunId);
        Assert.Equal(2, report.Summary.TotalCards);
        Assert.Equal(0, report.Summary.PlaceholderCards);
        Assert.Equal(before, after);
        Assert.False(report.IsStale);
        Assert.Equal(Path.GetFullPath(fixture.DatabasePath), report.SourcePath);
    }

    [Fact]
    public async Task AuditCurrentCoversNativeSummaryMetrics()
    {
        using var fixture = await QualityDatabaseFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            INSERT INTO sub_areas (id, building, sub_idx, floor, text, x, y)
            VALUES (1, '1号', 1, 1, '1F', 100, 100),
                   (2, '1号', 2, 2, '2F', 200, 100),
                   (3, '1号', 3, 3, '3F', 300, 100),
                   (4, '6号', 4, -2, 'BM', 400, 100);
            INSERT INTO pages (id, sub_area_id, page_name, count, raw_count, unique_count, layout)
            VALUES (1, 1, 'default', 3, 3, 2, 'grid'),
                   (2, 2, 'default', 2, 2, 2, 'grid');
            INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (1, 1, '0-0001-KT', 'BAD', '制冷', '0', '0', '中', '', ''),
                   (2, 1, 'A', 'OFF', '制冷', '0', '0', '中', 'green.png', '开机'),
                   (3, 1, 'A', 'OFF', '制冷', '0', '0', '中', 'green.png', '关机'),
                   (4, 2, 'B', 'OFF', '制冷', '22', '20', '高', 'green.png', '关机'),
                   (5, 2, 'C', 'OFF', '制冷', '22', '20', '高', 'green.png', '关机');
            """);
        var service = new SqliteQualityAuditService(() => fixture.DatabasePath);

        var report = await service.AuditAsync(NativeQualityAuditRequest.Current);

        Assert.NotNull(report);
        Assert.Null(report.RunId);
        Assert.Equal(5, report.Summary.TotalCards);
        Assert.Equal(1, report.Summary.PlaceholderCards);
        Assert.Equal(1, report.Summary.StateMismatch);
        Assert.Equal(1, report.Summary.UnknownCommunication);
        Assert.Equal(1, report.Summary.MissingIndicator);
        Assert.Equal(1, report.Summary.UnknownSwitch);
        Assert.Equal(1, report.Summary.DuplicateCardsSamePage);
        Assert.Equal(1, report.Summary.DuplicateRenderedPages);
        Assert.Equal(1, report.Summary.EmptySubAreas);
        Assert.Equal(1, report.Summary.InlineSubAreas);
        Assert.Equal(1, report.Summary.SuspiciousUniformPages);
        Assert.Equal(1, report.Summary.UniformResolvedPages);
        Assert.Contains(report.Issues, issue => issue.Code == "duplicate_rendered_pages" && issue.Severity == "INFO");
        Assert.Contains(report.Issues, issue => issue.Code == "inline_sub_area" && issue.Severity == "INFO");
    }

    [Fact]
    public async Task ExplicitDatabasePathOverridesTheConfiguredResolver()
    {
        using var fixture = await QualityDatabaseFixture.CreateAsync();
        var service = new SqliteQualityAuditService(() => Path.Combine(
            Path.GetDirectoryName(fixture.DatabasePath)!,
            "wrong.db"));

        var report = await service.AuditAsync(new NativeQualityAuditRequest(
            QualityAuditSourceKind.Current,
            fixture.DatabasePath));

        Assert.NotNull(report);
        Assert.Equal(Path.GetFullPath(fixture.DatabasePath), report.SourcePath);
    }

    [Fact]
    public async Task KnownFindingsSeparateBlockingRowsWithoutHidingDetectedSourceDefects()
    {
        using var fixture = await QualityDatabaseFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            INSERT INTO sub_areas (id, building, floor, text, x, y)
            VALUES (1, '2号', 2.5, '2.5F', 467, 146),
                   (2, '1号', 8, '8F', 679, 146),
                   (3, '1号', 17, '17F', 1103, 146);
            INSERT INTO pages (id, sub_area_id, page_name, quality_reason)
            VALUES (1, 1, 'default', 'known_source_indicator_missing'),
                   (2, 2, '一页', 'offline_template_stable'),
                   (3, 3, '二页', 'device_anomalies_preserved');
            INSERT INTO cards (id, page_id, name, switch, mode, indoor, set_temp, fan, indicator, comm)
            VALUES (1, 1, 'KNOWN-MISSING', 'OFF', '制冷', '27', '17', '高', '', ''),
                   (2, 1, 'BLOCKING-MISSING', 'OFF', '制冷', '27', '17', '高', '', ''),
                   (3, 2, 'OFFLINE-A', '-', '通风', '0', '0', '0', 'grey.png', '离线'),
                   (4, 2, 'OFFLINE-B', '-', '通风', '0', '0', '0', 'grey.png', '离线'),
                   (5, 3, 'BAD-FIELDS', 'ON', '-', '-1615.5', '3301.4', '-', 'red.png', '开机');
            """);
        var knownFindingsPath = Path.Combine(fixture.Root, "quality-known-findings.json");
        await File.WriteAllTextAsync(knownFindingsPath, JsonSerializer.Serialize(new
        {
            version = 1,
            findings = new object[]
            {
                new
                {
                    id = "accepted-indicator",
                    type = "device_missing_indicator",
                    status = "accepted_ems_source_defect",
                    building = "2号",
                    floor = 2.5,
                    subArea = "2.5F",
                    page = "default",
                    device = "KNOWN-MISSING",
                    reason = "Confirmed source defect",
                    evidence = new[] { "evidence-a" },
                },
                new
                {
                    id = "pending-indicator",
                    type = "device_missing_indicator",
                    status = "investigating",
                    building = "2号",
                    floor = 2.5,
                    subArea = "2.5F",
                    page = "default",
                    device = "BLOCKING-MISSING",
                    reason = "Not accepted",
                    evidence = Array.Empty<string>(),
                },
                new
                {
                    id = "accepted-offline",
                    type = "offline_template_page",
                    status = "accepted_long_offline",
                    building = "1号",
                    floor = 8,
                    subArea = "8F",
                    page = "一页",
                    reason = "Confirmed long offline page",
                    evidence = Array.Empty<string>(),
                },
                new
                {
                    id = "accepted-fields",
                    type = "device_invalid_fields",
                    status = "accepted_ems_source_defect",
                    building = "1号",
                    floor = 17,
                    subArea = "17F",
                    page = "二页",
                    device = "BAD-FIELDS",
                    reason = "Confirmed source values",
                    evidence = Array.Empty<string>(),
                },
            },
        }));
        var service = new SqliteQualityAuditService(
            () => fixture.DatabasePath,
            () => knownFindingsPath);

        var report = await service.AuditAsync(NativeQualityAuditRequest.Current);

        Assert.NotNull(report);
        Assert.Equal(2, report.Summary.UnknownCommunication);
        Assert.Equal(2, report.Summary.MissingIndicator);
        Assert.Equal(7, report.Summary.KnownFindings);
        Assert.Equal(2, report.Summary.BlockingKnownFindings);
        Assert.Equal(5, report.Summary.NonBlockingKnownFindings);
        Assert.Equal(1, report.Summary.DetectedOfflineTemplateStable);
        Assert.Equal(0, report.Summary.OfflineTemplateStable);
        Assert.Equal(1, report.Summary.DetectedInvalidCardFields);
        Assert.Equal(0, report.Summary.InvalidCardFields);
        Assert.Equal(1, report.Summary.DetectedActiveFieldIncompletePages);
        Assert.Equal(0, report.Summary.ActiveFieldIncompletePages);
        Assert.Equal(2, report.Summary.IssueCount);
        Assert.Contains(report.Issues, issue => issue.Code == "unknown_comm" && issue.Count == 1);
        Assert.Contains(report.Issues, issue => issue.Code == "missing_indicator" && issue.Count == 1);
        Assert.Equal(7, report.KnownFindingAnnotations.Count);
        var acceptedIndicator = Assert.Single(report.KnownFindingAnnotations, annotation =>
            annotation.IssueCode == "missing_indicator" &&
            annotation.DeviceName == "KNOWN-MISSING" &&
            !annotation.IsBlocking);
        Assert.Equal("evidence-a", Assert.Single(Assert.Single(acceptedIndicator.Findings).Evidence));
        Assert.Contains(report.KnownFindingAnnotations, annotation =>
            annotation.IssueCode == "missing_indicator" &&
            annotation.DeviceName == "BLOCKING-MISSING" &&
            annotation.IsBlocking);
    }

    [Fact]
    public async Task LoadLatestReturnsNullWhenThereIsNoCompletedRun()
    {
        using var fixture = await QualityDatabaseFixture.CreateAsync();
        await fixture.ExecuteAsync("""
            INSERT INTO collection_runs (id, completed_at, status)
            VALUES (1, '2026-07-10T08:00:00Z', 'in_progress');
            """);
        var service = new SqliteQualityAuditService(() => fixture.DatabasePath);

        var report = await service.LoadLatestAsync();

        Assert.Null(report);
    }

    private sealed class QualityDatabaseFixture : IDisposable
    {
        private QualityDatabaseFixture(string root)
        {
            Root = root;
            DatabasePath = Path.Combine(root, "quality.db");
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public static async Task<QualityDatabaseFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "ems-scout-native-quality-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new QualityDatabaseFixture(root);
            await fixture.ExecuteAsync("""
                CREATE TABLE sub_areas (
                    id INTEGER PRIMARY KEY,
                    building TEXT,
                    sub_idx INTEGER,
                    floor REAL,
                    text TEXT,
                    x INTEGER,
                    y INTEGER
                );
                CREATE TABLE pages (
                    id INTEGER PRIMARY KEY,
                    sub_area_id INTEGER,
                    page_name TEXT,
                    count INTEGER,
                    raw_count INTEGER,
                    unique_count INTEGER,
                    layout TEXT,
                    quality_reason TEXT
                );
                CREATE TABLE cards (
                    id INTEGER PRIMARY KEY,
                    page_id INTEGER,
                    name TEXT,
                    switch TEXT,
                    mode TEXT,
                    indoor TEXT,
                    set_temp TEXT,
                    fan TEXT,
                    indicator TEXT,
                    comm TEXT
                );
                CREATE TABLE collection_runs (
                    id INTEGER PRIMARY KEY,
                    completed_at TEXT NOT NULL,
                    status TEXT NOT NULL
                );
                CREATE TABLE run_sub_areas (
                    id INTEGER PRIMARY KEY,
                    run_id INTEGER NOT NULL,
                    building TEXT,
                    sub_idx INTEGER,
                    floor REAL,
                    text TEXT,
                    x INTEGER,
                    y INTEGER
                );
                CREATE TABLE run_pages (
                    id INTEGER PRIMARY KEY,
                    run_id INTEGER NOT NULL,
                    run_sub_area_id INTEGER,
                    page_name TEXT,
                    count INTEGER,
                    raw_count INTEGER,
                    unique_count INTEGER,
                    layout TEXT,
                    quality_reason TEXT
                );
                CREATE TABLE run_cards (
                    id INTEGER PRIMARY KEY,
                    run_id INTEGER NOT NULL,
                    run_page_id INTEGER,
                    name TEXT,
                    switch TEXT,
                    mode TEXT,
                    indoor TEXT,
                    set_temp TEXT,
                    fan TEXT,
                    indicator TEXT,
                    comm TEXT
                );
                """);
            return fixture;
        }

        public async Task ExecuteAsync(string sql)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
