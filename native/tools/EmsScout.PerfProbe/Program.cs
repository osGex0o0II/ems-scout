using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EmsScout.Application;
using EmsScout.Application.Devices;
using EmsScout.Application.Groups;
using EmsScout.Application.Settings;
using EmsScout.Infrastructure.Realtime;
using EmsScout.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

const int WarmIterations = 20;

PerfProbeOptions options;
try
{
    options = PerfProbeOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    PerfProbeOptions.PrintHelp();
    return 2;
}

if (options.ShowHelp)
{
    PerfProbeOptions.PrintHelp();
    return 0;
}

if (options.DatabasePath is null || options.RealtimeDirectory is null || options.OutputDirectory is null)
{
    Console.Error.WriteLine("ERROR: --db, --realtime-dir, and --out are required.");
    PerfProbeOptions.PrintHelp();
    return 2;
}

var sourceDatabase = Path.GetFullPath(options.DatabasePath);
var realtimeDirectory = Path.GetFullPath(options.RealtimeDirectory);
var outputRoot = Path.GetFullPath(options.OutputDirectory);
if (!File.Exists(sourceDatabase))
{
    Console.Error.WriteLine("ERROR: database not found: " + sourceDatabase);
    return 2;
}

if (!Directory.Exists(realtimeDirectory))
{
    Console.Error.WriteLine("ERROR: realtime directory not found: " + realtimeDirectory);
    return 2;
}

var runDirectory = Path.Combine(
    outputRoot,
    $"perf-probe-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
Directory.CreateDirectory(runDirectory);
var probeDatabase = Path.Combine(runDirectory, "probe.db");
var sourceInfo = new FileInfo(sourceDatabase);
var sourceCounts = ReadCounts(sourceDatabase);
BackupDatabase(sourceDatabase, probeDatabase);
await new SqliteSchemaMigrator(() => probeDatabase).MigrateAsync();

var measurements = new List<ProbeMeasurement>();
var scenarios = new (string Name, Func<ProbeContext, Task<Func<Task>>> Prepare)[]
{
    ("load-configuration", context => Task.FromResult<Func<Task>>(async () =>
    {
        _ = await context.Groups.LoadConfigurationAsync();
    })),
    ("floors-one-building", context => Task.FromResult<Func<Task>>(async () =>
    {
        _ = await context.Groups.LoadFloorsAsync("1号");
    })),
    ("floors-per-rule", async context =>
    {
        var rules = (await context.Groups.LoadConfigurationAsync()).RuleRecords.ToArray();
        return async () =>
        {
            foreach (var rule in rules)
            {
                _ = await context.Groups.LoadFloorsAsync(rule.Building);
            }
        };
    }),
    ("floors-distinct-buildings", async context =>
    {
        var buildings = (await context.Groups.LoadConfigurationAsync()).RuleRecords
            .Select(rule => rule.Building)
            .Where(building => !string.IsNullOrWhiteSpace(building))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return async () =>
        {
            foreach (var building in buildings)
            {
                _ = await context.Groups.LoadFloorsAsync(building);
            }
        };
    }),
    ("floors-selected-group-per-rule", async context =>
    {
        var configuration = await context.Groups.LoadConfigurationAsync();
        var selected = SelectBenchmarkGroup(configuration);
        var rules = selected is null
            ? []
            : configuration.RuleRecords.Where(rule => rule.GroupId == selected.Id).ToArray();
        return async () =>
        {
            foreach (var rule in rules)
            {
                _ = await context.Groups.LoadFloorsAsync(rule.Building);
            }
        };
    }),
    ("floors-selected-group-distinct-buildings", async context =>
    {
        var configuration = await context.Groups.LoadConfigurationAsync();
        var selected = SelectBenchmarkGroup(configuration);
        var buildings = selected is null
            ? []
            : configuration.RuleRecords
                .Where(rule => rule.GroupId == selected.Id)
                .Select(rule => rule.Building)
                .Where(building => !string.IsNullOrWhiteSpace(building))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return async () =>
        {
            foreach (var building in buildings)
            {
                _ = await context.Groups.LoadFloorsAsync(building);
            }
        };
    }),
    ("rule-count-one", async context =>
    {
        var rule = (await context.Groups.LoadConfigurationAsync()).RuleRecords.FirstOrDefault();
        if (rule is null)
        {
            return () => Task.CompletedTask;
        }

        return async () => { _ = await context.Devices.CountAreaGroupRuleMatchesAsync([rule]); };
    }),
    ("rule-count-all", async context =>
    {
        var rules = (await context.Groups.LoadConfigurationAsync()).RuleRecords.ToArray();
        return async () => { _ = await context.Devices.CountAreaGroupRuleMatchesAsync(rules); };
    }),
    ("synthetic-rule-count-500", async context =>
    {
        var sourceRules = (await context.Groups.LoadConfigurationAsync()).RuleRecords;
        var rules = BuildSyntheticRules(sourceRules, 500);
        return async () => { _ = await context.Devices.CountAreaGroupRuleMatchesAsync(rules); };
    }),
    ("synthetic-rule-count-1000", async context =>
    {
        var sourceRules = (await context.Groups.LoadConfigurationAsync()).RuleRecords;
        var rules = BuildSyntheticRules(sourceRules, 1_000);
        return async () => { _ = await context.Devices.CountAreaGroupRuleMatchesAsync(rules); };
    }),
    ("search-50", context => Task.FromResult<Func<Task>>(async () =>
    {
        _ = await context.Devices.SearchAsync(new DeviceQuery(Limit: 50));
    })),
    ("combined-500", context => Task.FromResult<Func<Task>>(async () =>
    {
        _ = await context.Devices.SearchWithFilterOptionsAsync(new DeviceQuery(Limit: 500));
    })),
    ("next-page-500", async context =>
    {
        _ = await context.Devices.SearchAsync(new DeviceQuery(Limit: 500, Offset: 0));
        return async () =>
        {
            _ = await context.Devices.SearchAsync(new DeviceQuery(Limit: 500, Offset: 500));
        };
    }),
    ("overview", context => Task.FromResult<Func<Task>>(async () =>
    {
        _ = await context.Overview.LoadAsync();
    }))
};

foreach (var scenario in scenarios)
{
    using var context = new ProbeContext(probeDatabase, realtimeDirectory);
    var operation = await scenario.Prepare(context);
    measurements.Add(await MeasureAsync(scenario.Name, "cold", 0, operation));
    for (var iteration = 1; iteration <= WarmIterations; iteration++)
    {
        measurements.Add(await MeasureAsync(scenario.Name, "warm", iteration, operation));
    }
}

var configuration = await new SqliteAreaGroupRepository(() => probeDatabase).LoadConfigurationAsync();
var selectedBenchmarkGroup = SelectBenchmarkGroup(configuration);
var report = new PerfProbeReport(
    GeneratedAt: DateTimeOffset.Now,
    SourceDatabase: sourceDatabase,
    SourceDatabaseLength: sourceInfo.Length,
    SourceDatabaseLastWriteUtc: sourceInfo.LastWriteTimeUtc,
    ProbeDatabase: probeDatabase,
    RealtimeDirectory: realtimeDirectory,
    RunDirectory: runDirectory,
    WarmIterations: WarmIterations,
    ColdDefinition: "First measured call on a fresh repository/dashboard graph for this scenario; process, JIT, SQLite native runtime, and OS file caches are shared across the run.",
    WarmDefinition: "Twenty subsequent calls on the same scenario repository/dashboard graph.",
    SourceCounts: sourceCounts,
    AreaGroupCount: configuration.Groups.Count,
    AreaRuleCount: configuration.RuleRecords.Count,
    DistinctRuleBuildings: configuration.RuleRecords
        .Select(rule => rule.Building)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count(),
    SelectedGroup: selectedBenchmarkGroup is null
        ? null
        : new SelectedGroupMetadata(
            selectedBenchmarkGroup.Id,
            selectedBenchmarkGroup.Name,
            configuration.RuleRecords.Count(rule => rule.GroupId == selectedBenchmarkGroup.Id),
            configuration.RuleRecords
                .Where(rule => rule.GroupId == selectedBenchmarkGroup.Id)
                .Select(rule => rule.Building)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()),
    Summaries: BuildSummaries(measurements),
    Measurements: measurements);
var reportPath = Path.Combine(runDirectory, "performance.json");
await File.WriteAllTextAsync(
    reportPath,
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine("PERF_PROBE_OK");
Console.WriteLine("source_db=" + sourceDatabase);
Console.WriteLine("probe_db=" + probeDatabase);
Console.WriteLine("report=" + reportPath);
return 0;

static async Task<ProbeMeasurement> MeasureAsync(
    string scenario,
    string phase,
    int iteration,
    Func<Task> operation)
{
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var stopwatch = Stopwatch.StartNew();
    var task = operation();
    var returnMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    var completedOnReturn = task.IsCompleted;
    await task.ConfigureAwait(false);
    stopwatch.Stop();
    return new ProbeMeasurement(
        scenario,
        phase,
        iteration,
        Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
        Math.Round(returnMilliseconds, 3),
        completedOnReturn,
        Math.Round((GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / 1_048_576d, 3));
}

static IReadOnlyList<ProbeScenarioSummary> BuildSummaries(IReadOnlyList<ProbeMeasurement> measurements)
{
    return measurements
        .GroupBy(item => item.Scenario, StringComparer.Ordinal)
        .Select(group =>
        {
            var cold = group.Single(item => item.Phase == "cold");
            var warm = group.Where(item => item.Phase == "warm").ToArray();
            return new ProbeScenarioSummary(
                group.Key,
                cold,
                Percentile(warm.Select(item => item.TotalMilliseconds), 0.50),
                Percentile(warm.Select(item => item.TotalMilliseconds), 0.95),
                Percentile(warm.Select(item => item.ReturnMilliseconds), 0.50),
                Percentile(warm.Select(item => item.ReturnMilliseconds), 0.95),
                Percentile(warm.Select(item => item.AllocatedMegabytes), 0.50),
                Percentile(warm.Select(item => item.AllocatedMegabytes), 0.95),
                warm.Count(item => item.CompletedOnReturn));
        })
        .ToArray();
}

static double Percentile(IEnumerable<double> source, double percentile)
{
    var values = source.OrderBy(value => value).ToArray();
    if (values.Length == 0)
    {
        return 0;
    }

    var rank = Math.Max(0, (int)Math.Ceiling(percentile * values.Length) - 1);
    return Math.Round(values[rank], 3);
}

static AreaGroupRecord? SelectBenchmarkGroup(AreaGroupSet configuration)
{
    var groupsWithRules = configuration.RuleRecords
        .Select(rule => rule.GroupId)
        .ToHashSet();
    return configuration.Groups.FirstOrDefault(group => group.Enabled && groupsWithRules.Contains(group.Id));
}

static IReadOnlyList<AreaGroupRuleRecord> BuildSyntheticRules(
    IReadOnlyList<AreaGroupRuleRecord> source,
    int count)
{
    if (source.Count == 0 || count <= 0)
    {
        return [];
    }

    return Enumerable.Range(0, count)
        .Select(index => source[index % source.Count] with
        {
            Id = -(index + 1L),
            RuleOrder = index + 1
        })
        .ToArray();
}

static IReadOnlyDictionary<string, long?> ReadCounts(string databasePath)
{
    string[] tables =
    [
        "cards", "pages", "sub_areas", "collection_runs", "monitor_groups",
        "area_group_rules", "floor_catalog", "device_watch_rules"
    ];
    var counts = new Dictionary<string, long?>(StringComparer.Ordinal);
    using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
    connection.Open();
    foreach (var table in tables)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        try
        {
            counts[table] = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            counts[table] = null;
        }
    }

    return counts;
}

static void BackupDatabase(string sourcePath, string destinationPath)
{
    using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
    using var destination = new SqliteConnection($"Data Source={destinationPath};Mode=ReadWriteCreate;Pooling=False");
    source.Open();
    destination.Open();
    source.BackupDatabase(destination);
}

internal sealed class ProbeContext : IDisposable
{
    public ProbeContext(string databasePath, string realtimeDirectory)
    {
        Groups = new SqliteAreaGroupRepository(() => databasePath);
        var realtimeRoot = Directory.GetParent(realtimeDirectory)?.FullName ?? realtimeDirectory;
        var realtime = new RealtimeLatestJsonSource(realtimeRoot, realtimeDirectory);
        var watch = new SqliteDeviceWatchRepository(() => databasePath);
        var snapshots = new SqliteRealtimeSnapshotStore(() => databasePath);
        Devices = new SqliteDeviceReadRepository(() => databasePath, realtime, watch, snapshots);
        var summary = new SqliteDashboardSummaryRepository(() => databasePath);
        Overview = new DashboardOverviewService(
            Devices,
            Groups,
            new AppSettingsService(Path.Combine(
                Path.GetDirectoryName(databasePath)!,
                "probe-settings.json")),
            collectionRunRepository: null,
            summaryRepository: summary,
            revisionSource: Devices);
    }

    public SqliteAreaGroupRepository Groups { get; }
    public SqliteDeviceReadRepository Devices { get; }
    public DashboardOverviewService Overview { get; }

    public void Dispose() => Devices.Dispose();
}

internal sealed record ProbeMeasurement(
    string Scenario,
    string Phase,
    int Iteration,
    double TotalMilliseconds,
    double ReturnMilliseconds,
    bool CompletedOnReturn,
    double AllocatedMegabytes);

internal sealed record ProbeScenarioSummary(
    string Scenario,
    ProbeMeasurement Cold,
    double WarmTotalP50Milliseconds,
    double WarmTotalP95Milliseconds,
    double WarmReturnP50Milliseconds,
    double WarmReturnP95Milliseconds,
    double WarmAllocatedP50Megabytes,
    double WarmAllocatedP95Megabytes,
    int WarmCompletedOnReturnCount);

internal sealed record PerfProbeReport(
    DateTimeOffset GeneratedAt,
    string SourceDatabase,
    long SourceDatabaseLength,
    DateTime SourceDatabaseLastWriteUtc,
    string ProbeDatabase,
    string RealtimeDirectory,
    string RunDirectory,
    int WarmIterations,
    string ColdDefinition,
    string WarmDefinition,
    IReadOnlyDictionary<string, long?> SourceCounts,
    int AreaGroupCount,
    int AreaRuleCount,
    int DistinctRuleBuildings,
    SelectedGroupMetadata? SelectedGroup,
    IReadOnlyList<ProbeScenarioSummary> Summaries,
    IReadOnlyList<ProbeMeasurement> Measurements);

internal sealed record SelectedGroupMetadata(
    long Id,
    string Name,
    int RuleCount,
    int DistinctBuildingCount);

internal sealed class PerfProbeOptions
{
    public string? DatabasePath { get; private set; }
    public string? RealtimeDirectory { get; private set; }
    public string? OutputDirectory { get; private set; }
    public bool ShowHelp { get; private set; }

    public static PerfProbeOptions Parse(string[] args)
    {
        var options = new PerfProbeOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help")
            {
                options.ShowHelp = true;
                continue;
            }

            var equalsIndex = arg.IndexOf('=', StringComparison.Ordinal);
            var name = equalsIndex < 0 ? arg : arg[..equalsIndex];
            var value = equalsIndex < 0
                ? index + 1 < args.Length ? args[++index] : throw new ArgumentException($"Missing value for {name}.")
                : arg[(equalsIndex + 1)..];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Missing value for {name}.");
            }

            switch (name)
            {
                case "--db":
                    options.DatabasePath = value;
                    break;
                case "--realtime-dir":
                    options.RealtimeDirectory = value;
                    break;
                case "--out":
                    options.OutputDirectory = value;
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + name);
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run --project native/tools/EmsScout.PerfProbe -- --db <ac.db> --realtime-dir <dir> --out <dir>");
        Console.WriteLine("Creates a unique run directory, copies the source with SQLite Backup, and writes performance.json.");
    }
}
