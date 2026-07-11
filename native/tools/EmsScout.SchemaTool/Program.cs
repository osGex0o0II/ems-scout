using System.Text.Json;
using System.Text.Json.Serialization;
using EmsScout.Infrastructure.Migrations;

return await SchemaToolProgram.RunAsync(args);

internal static class SchemaToolProgram
{
    private const int MaxTextIdentityFindings = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<int> RunAsync(string[] args)
    {
        SchemaToolOptions options;
        try
        {
            options = SchemaToolOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            SchemaToolOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            SchemaToolOptions.PrintHelp();
            return 0;
        }

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            Console.Error.WriteLine("ERROR: --db is required.");
            return 2;
        }

        if (options.Command == SchemaToolCommand.Migrate && !options.Apply)
        {
            Console.Error.WriteLine("ERROR: migrate is write-capable and requires the explicit --apply flag.");
            return 2;
        }

        try
        {
            if (options.Command == SchemaToolCommand.Audit)
            {
                var report = await new SqliteSchemaAuditor().AuditAsync(options.DatabasePath);
                WriteAudit(report, options.Json);
                return report.CanMigrate ? 0 : 1;
            }

            var result = await new SqliteSchemaMigrator().MigrateAsync(options.DatabasePath, options.BackupPath);
            WriteMigration(result, options.Json);
            return 0;
        }
        catch (SchemaMigrationException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            if (!string.IsNullOrWhiteSpace(ex.BackupPath))
            {
                Console.Error.WriteLine("backup=" + ex.BackupPath);
            }

            if (ex.InnerException is not null)
            {
                Console.Error.WriteLine("cause=" + ex.InnerException.Message);
            }

            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static void WriteAudit(SchemaAuditReport report, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return;
        }

        Console.WriteLine(report.CanMigrate ? "AUDIT_OK" : "AUDIT_BLOCKED");
        Console.WriteLine("database=" + report.DatabasePath);
        Console.WriteLine("shape=" + report.DatabaseShape);
        Console.WriteLine("user_version=" + report.UserVersion);
        Console.WriteLine("latest_supported=" + report.LatestSupportedVersion);
        Console.WriteLine("journal_mode=" + report.JournalMode);
        Console.WriteLine("quick_check=" + report.QuickCheck);
        Console.WriteLine("current=" + report.IsCurrent.ToString().ToLowerInvariant());
        Console.WriteLine("pending=" + report.PendingChanges.Count);
        Console.WriteLine("identity_unresolved=" + report.UnresolvedIdentityAmbiguities);
        Console.WriteLine("identity_auto_resolved=" + report.AutoResolvedIdentityAmbiguities);
        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"issue={issue.Severity.ToString().ToLowerInvariant()}:{issue.Code}:{issue.Message}");
        }

        foreach (var change in report.PendingChanges)
        {
            Console.WriteLine($"change={change.Kind.ToString().ToLowerInvariant()}:{change.ObjectName}:{change.Description}");
        }
    }

    private static void WriteMigration(SchemaMigrationResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        Console.WriteLine("MIGRATION_OK");
        Console.WriteLine("database=" + result.DatabasePath);
        Console.WriteLine("backup=" + (result.BackupPath ?? "not-required"));
        Console.WriteLine("before_shape=" + result.Before.DatabaseShape);
        Console.WriteLine("after_shape=" + result.After.DatabaseShape);
        Console.WriteLine("user_version=" + result.After.UserVersion);
        Console.WriteLine("applied_changes=" + result.AppliedChanges.Count);
        if (result.IdentityReport is { } identity)
        {
            Console.WriteLine("identity_current=" + identity.CurrentDeviceUidCount + "/" + identity.CurrentCardCount);
            Console.WriteLine("identity_runs=" + identity.RunDeviceUidCount + "/" + identity.RunCardCount);
            Console.WriteLine("identity_registry=" + identity.RegistryCount);
            Console.WriteLine("identity_aliases=" + identity.SourceAliasCount);
            Console.WriteLine("identity_user_refs=" + identity.UserReferenceResolvedCount + "/" + identity.UserReferenceCount);
            Console.WriteLine("identity_unresolved=" + identity.UnresolvedCount);
            Console.WriteLine("identity_auto_resolved=" + identity.AutoResolvedCount);
            foreach (var ambiguity in identity.Ambiguities.Take(MaxTextIdentityFindings))
            {
                Console.WriteLine(
                    $"identity_finding={ambiguity.Status}:{ambiguity.EntityTable}:{ambiguity.EntityKey}:{ambiguity.ReasonCode}:" +
                    $"{string.Join(',', ambiguity.CandidateDeviceUids)}:{ambiguity.ResolutionNote}");
            }

            if (identity.Ambiguities.Count > MaxTextIdentityFindings)
            {
                Console.WriteLine("identity_findings_omitted=" + (identity.Ambiguities.Count - MaxTextIdentityFindings));
            }
        }
    }
}

internal enum SchemaToolCommand
{
    Audit,
    Migrate,
}

internal sealed class SchemaToolOptions
{
    public SchemaToolCommand Command { get; private set; } = SchemaToolCommand.Audit;
    public string? DatabasePath { get; private set; }
    public string? BackupPath { get; private set; }
    public bool Apply { get; private set; }
    public bool Json { get; private set; }
    public bool ShowHelp { get; private set; }

    public static SchemaToolOptions Parse(string[] args)
    {
        var options = new SchemaToolOptions();
        var index = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            options.Command = args[0].ToLowerInvariant() switch
            {
                "audit" => SchemaToolCommand.Audit,
                "migrate" => SchemaToolCommand.Migrate,
                _ => throw new ArgumentException("Unknown command: " + args[0]),
            };
            index = 1;
        }

        while (index < args.Length)
        {
            var argument = args[index++];
            switch (argument)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                case "--apply":
                    options.Apply = true;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--db":
                    options.DatabasePath = RequireValue(args, ref index, argument);
                    break;
                case "--backup":
                    options.BackupPath = RequireValue(args, ref index, argument);
                    break;
                default:
                    if (argument.StartsWith("--db=", StringComparison.Ordinal))
                    {
                        options.DatabasePath = RequireInlineValue(argument, "--db=");
                    }
                    else if (argument.StartsWith("--backup=", StringComparison.Ordinal))
                    {
                        options.BackupPath = RequireInlineValue(argument, "--backup=");
                    }
                    else
                    {
                        throw new ArgumentException("Unknown argument: " + argument);
                    }

                    break;
            }
        }

        if (options.Command == SchemaToolCommand.Audit && options.Apply)
        {
            throw new ArgumentException("--apply is only valid with the migrate command.");
        }

        if (options.Command == SchemaToolCommand.Audit && !string.IsNullOrWhiteSpace(options.BackupPath))
        {
            throw new ArgumentException("--backup is only valid with the migrate command.");
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("EMS Scout schema audit and migration tool");
        Console.WriteLine();
        Console.WriteLine("Read-only audit (default):");
        Console.WriteLine("  EmsScout.SchemaTool [audit] --db <path> [--json]");
        Console.WriteLine();
        Console.WriteLine("Explicit migration with mandatory online backup:");
        Console.WriteLine("  EmsScout.SchemaTool migrate --db <path> --apply [--backup <path>] [--json]");
    }

    private static string RequireValue(string[] args, ref int index, string argument)
    {
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException(argument + " requires a value.");
        }

        return args[index++];
    }

    private static string RequireInlineValue(string argument, string prefix)
    {
        var value = argument[prefix.Length..];
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(prefix.TrimEnd('=') + " requires a value.")
            : value;
    }
}
