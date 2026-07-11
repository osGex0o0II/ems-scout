using System.Text.Encodings.Web;
using System.Text.Json;
using EmsScout.Application.Quality;
using EmsScout.Infrastructure.Importing;
using EmsScout.Infrastructure.Migrations;
using EmsScout.Infrastructure.Quality;

return await DataToolProgram.RunAsync(args);

internal static class DataToolProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        DataToolOptions options;
        try
        {
            options = DataToolOptions.Parse(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine("ERROR: " + error.Message);
            DataToolOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            DataToolOptions.PrintHelp();
            return 0;
        }

        try
        {
            if (options.Command == DataToolCommand.Audit)
            {
                var service = new SqliteQualityAuditService(
                    () => options.DatabasePath!,
                    () => options.KnownFindingsPath);
                var request = new NativeQualityAuditRequest(options.AuditSource);
                var audit = await service.AuditAsync(request)
                            ?? throw new InvalidOperationException(
                                "The selected SQLite quality audit source has no data.");
                Console.WriteLine(JsonSerializer.Serialize(audit, JsonOptions));
                return audit.Summary.IssueCount == 0 ? 0 : 1;
            }

            var importer = new CollectionSnapshotImporter();
            CollectionImportParityReport report;
            if (options.Command == DataToolCommand.Validate)
            {
                report = await importer.ValidateAsync(options.SnapshotPath!, options.Buildings);
            }
            else
            {
                report = await importer.ImportAsync(new(
                    options.SnapshotPath!,
                    options.DatabasePath,
                    options.Buildings,
                    options.Apply,
                    options.BackupPath));
            }
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (CollectionSnapshotContractException error)
        {
            Console.Error.WriteLine("CONTRACT_ERROR: " + error.Message);
            return 2;
        }
        catch (SchemaMigrationException error)
        {
            Console.Error.WriteLine("MIGRATION_ERROR: " + error.Message);
            if (error.BackupPath is not null) Console.Error.WriteLine("backup=" + error.BackupPath);
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine("ERROR: " + error.Message);
            return 1;
        }
    }
}

internal enum DataToolCommand
{
    Validate,
    Import,
    Audit,
}

internal sealed class DataToolOptions
{
    public DataToolCommand Command { get; private set; }
    public string? SnapshotPath { get; private set; }
    public string? DatabasePath { get; private set; }
    public string? BackupPath { get; private set; }
    public string? KnownFindingsPath { get; private set; }
    public List<string> Buildings { get; } = [];
    public QualityAuditSourceKind AuditSource { get; private set; } = QualityAuditSourceKind.LatestCompletedRun;
    public bool Apply { get; private set; }
    public bool ShowHelp { get; private set; }

    private bool AuditSourceSpecified { get; set; }

    public static DataToolOptions Parse(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("A command is required: validate, import, or audit.");
        var options = new DataToolOptions
        {
            Command = args[0].ToLowerInvariant() switch
            {
                "validate" => DataToolCommand.Validate,
                "import" => DataToolCommand.Import,
                "audit" or "quality" => DataToolCommand.Audit,
                "-h" or "--help" => DataToolCommand.Validate,
                _ => throw new ArgumentException("Unknown command: " + args[0]),
            },
            ShowHelp = args[0] is "-h" or "--help",
        };
        if (options.ShowHelp) return options;

        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            var (name, inlineValue) = SplitOption(argument);
            switch (name)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                case "--apply":
                    if (inlineValue is not null) throw new ArgumentException("--apply does not take a value.");
                    options.Apply = true;
                    break;
                case "--json":
                    if (inlineValue is not null) throw new ArgumentException("--json does not take a value.");
                    break;
                case "--snapshot":
                    options.SnapshotPath = ReadValue(args, ref index, name, inlineValue);
                    break;
                case "--db":
                    options.DatabasePath = ReadValue(args, ref index, name, inlineValue);
                    break;
                case "--backup":
                    options.BackupPath = ReadValue(args, ref index, name, inlineValue);
                    break;
                case "--known-findings":
                    options.KnownFindingsPath = ReadValue(args, ref index, name, inlineValue);
                    break;
                case "--source":
                    options.AuditSource = ParseAuditSource(ReadValue(args, ref index, name, inlineValue));
                    options.AuditSourceSpecified = true;
                    break;
                case "--building":
                case "--buildings":
                    AddBuildings(options.Buildings, ReadValue(args, ref index, name, inlineValue));
                    break;
                default:
                    throw new ArgumentException("Unknown option: " + name);
            }
        }

        if (options.Command == DataToolCommand.Audit)
        {
            if (string.IsNullOrWhiteSpace(options.DatabasePath)) throw new ArgumentException("audit requires --db.");
            if (options.SnapshotPath is not null) throw new ArgumentException("--snapshot is not valid with audit.");
            if (options.Apply) throw new ArgumentException("--apply is not valid with audit.");
            if (options.BackupPath is not null) throw new ArgumentException("--backup is not valid with audit.");
            if (options.Buildings.Count > 0) throw new ArgumentException("--buildings is not valid with audit.");
        }
        else if (options.Command == DataToolCommand.Validate)
        {
            if (string.IsNullOrWhiteSpace(options.SnapshotPath)) throw new ArgumentException("validate requires --snapshot.");
            if (options.Apply) throw new ArgumentException("--apply is only valid with import.");
            if (options.DatabasePath is not null) throw new ArgumentException("--db is only valid with import.");
            if (options.BackupPath is not null) throw new ArgumentException("--backup is only valid with import --apply.");
            RejectAuditOptions(options);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.SnapshotPath)) throw new ArgumentException("import requires --snapshot.");
            if (string.IsNullOrWhiteSpace(options.DatabasePath)) throw new ArgumentException("import requires --db.");
            if (!options.Apply && options.BackupPath is not null)
            {
                throw new ArgumentException("--backup requires import --apply.");
            }
            RejectAuditOptions(options);
        }
        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("EMS Scout CollectionSnapshot v1 data tool");
        Console.WriteLine();
        Console.WriteLine("Strict read-only validation:");
        Console.WriteLine("  EmsScout.DataTool validate --snapshot <v1.json> [--buildings 1号,2号] --json");
        Console.WriteLine();
        Console.WriteLine("Read-only shadow import and parity report (default import behavior):");
        Console.WriteLine("  EmsScout.DataTool import --snapshot <v1.json> --db <ac.db> [--buildings 1号,2号] --json");
        Console.WriteLine();
        Console.WriteLine("Explicit transactional apply:");
        Console.WriteLine("  EmsScout.DataTool import --snapshot <v1.json> --db <ac.db> [--buildings 1号,2号] --apply [--backup <path>] --json");
        Console.WriteLine();
        Console.WriteLine("Native SQLite quality audit (nonzero exit when blocking issues remain):");
        Console.WriteLine("  EmsScout.DataTool audit --db <ac.db> [--source latest|current] [--known-findings <path>] --json");
    }

    private static (string Name, string? Value) SplitOption(string argument)
    {
        if (!argument.StartsWith('-')) throw new ArgumentException("Unexpected positional argument: " + argument);
        var equals = argument.IndexOf('=');
        return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
    }

    private static string ReadValue(string[] args, ref int index, string option, string? inlineValue)
    {
        var value = inlineValue ?? (index + 1 < args.Length ? args[++index] : null);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(option + " requires a value.")
            : value;
    }

    private static void AddBuildings(ICollection<string> target, string value)
    {
        foreach (var building in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!target.Contains(building, StringComparer.Ordinal)) target.Add(building);
        }
        if (target.Count == 0) throw new ArgumentException("--buildings requires at least one building.");
    }

    private static QualityAuditSourceKind ParseAuditSource(string value) => value.ToLowerInvariant() switch
    {
        "latest" or "latest-run" or "latest_completed_run" => QualityAuditSourceKind.LatestCompletedRun,
        "current" => QualityAuditSourceKind.Current,
        _ => throw new ArgumentException("--source must be latest or current."),
    };

    private static void RejectAuditOptions(DataToolOptions options)
    {
        if (options.AuditSourceSpecified) throw new ArgumentException("--source is only valid with audit.");
        if (options.KnownFindingsPath is not null)
        {
            throw new ArgumentException("--known-findings is only valid with audit.");
        }
    }
}
