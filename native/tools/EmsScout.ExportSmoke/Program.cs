using System.IO.Compression;
using System.Xml.Linq;
using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Sqlite;
using EmsScout.Infrastructure.Realtime;

ExportSmokeOptions options;
try
{
    options = ExportSmokeOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    ExportSmokeOptions.PrintHelp();
    return 2;
}
if (options.ShowHelp)
{
    ExportSmokeOptions.PrintHelp();
    return 0;
}

if (string.IsNullOrWhiteSpace(options.DatabasePath))
{
    Console.Error.WriteLine("ERROR: --db is required.");
    return 2;
}

if (string.IsNullOrWhiteSpace(options.OutputDirectory))
{
    Console.Error.WriteLine("ERROR: --out is required.");
    return 2;
}

try
{
    var dbPath = Path.GetFullPath(options.DatabasePath);
    var outputDirectory = Path.GetFullPath(options.OutputDirectory);
    var workspaceRoot = Path.GetFullPath(options.WorkspaceRoot ?? LocateWorkspaceRoot());
    var realtimeDirectory = Path.GetFullPath(options.RealtimeDirectory ?? Path.GetDirectoryName(dbPath) ?? workspaceRoot);

    if (!File.Exists(dbPath))
    {
        Console.Error.WriteLine("ERROR: database not found: " + dbPath);
        return 2;
    }

    Directory.CreateDirectory(outputDirectory);
    var realtime = new RealtimeLatestJsonSource(workspaceRoot, realtimeDirectory);
    var repository = new SqliteDeviceReadRepository(() => dbPath, realtime);
    var service = new SqliteDeviceExportService(repository);
    var query = new DeviceQuery(
        Building: options.Building,
        CommunicationState: options.CommunicationState,
        Floor: options.Floor,
        SubArea: options.SubArea,
        DeviceName: options.DeviceName,
        Zuo: options.Zuo,
        RealtimeLock: options.RealtimeLock,
        AreaType: options.AreaType,
        Mode: options.Mode,
        Fan: options.Fan,
        SetTemperature: options.SetTemperature,
        IndoorTemperature: options.IndoorTemperature);

    var result = await service.ExportAsync(query, outputDirectory);
    ValidateWorkbook(result.Path, result.RowCount, options.AllowEmpty);
    Console.WriteLine("EXPORT_OK");
    Console.WriteLine("path=" + result.Path);
    Console.WriteLine("rows=" + result.RowCount);
    Console.WriteLine("sheets=" + string.Join(",", result.Sheets));
    Console.WriteLine("db=" + dbPath);
    Console.WriteLine("realtime_dir=" + realtimeDirectory);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return 1;
}

static void ValidateWorkbook(string path, int rowCount, bool allowEmpty)
{
    string[] expectedHeader =
    [
        "楼栋",
        "座号",
        "楼层",
        "页面",
        "设备名",
        "区域",
        "开关机状态",
        "模式",
        "风速",
        "设置温度",
        "环境温度",
        "集控锁定状态",
    ];

    if (!File.Exists(path))
    {
        throw new FileNotFoundException("Export workbook was not created.", path);
    }

    if (!allowEmpty && rowCount <= 0)
    {
        throw new InvalidOperationException("Export returned zero rows. Use --allow-empty only for intentional empty-filter smoke tests.");
    }

    using var archive = ZipFile.OpenRead(path);
    RequireEntry(archive, "[Content_Types].xml");
    RequireEntry(archive, "xl/workbook.xml");
    RequireEntry(archive, "xl/worksheets/sheet1.xml");
    RejectEntry(archive, "xl/worksheets/sheet2.xml");
    var workbook = ReadEntry(archive, "xl/workbook.xml");
    if (!workbook.Contains("name=\"devices\"", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Workbook is missing the expected devices sheet.");
    }

    if (workbook.Contains("name=\"summary\"", StringComparison.Ordinal) ||
        workbook.Contains("name=\"filters\"", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Workbook still contains legacy summary/filters sheets.");
    }

    var rows = ReadWorksheetRows(archive);
    if (rows.Count == 0)
    {
        throw new InvalidOperationException("Workbook has no rows.");
    }

    if (!rows[0].SequenceEqual(expectedHeader, StringComparer.Ordinal))
    {
        throw new InvalidOperationException("Workbook header does not match the Data Management 12-column export contract.");
    }

    if (rows.Any(row => row.Count != expectedHeader.Length))
    {
        throw new InvalidOperationException("Workbook contains rows that do not match the 12-column export contract.");
    }
}

static void RequireEntry(ZipArchive archive, string name)
{
    if (archive.GetEntry(name) is null)
    {
        throw new InvalidOperationException("Workbook missing ZIP entry: " + name);
    }
}

static void RejectEntry(ZipArchive archive, string name)
{
    if (archive.GetEntry(name) is not null)
    {
        throw new InvalidOperationException("Workbook contains legacy ZIP entry: " + name);
    }
}

static IReadOnlyList<IReadOnlyList<string>> ReadWorksheetRows(ZipArchive archive)
{
    var xml = ReadEntry(archive, "xl/worksheets/sheet1.xml");
    var document = XDocument.Parse(xml);
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    return document
        .Descendants(ns + "row")
        .Select(row => (IReadOnlyList<string>)row
            .Elements(ns + "c")
            .Select(cell => cell.Descendants(ns + "t").FirstOrDefault()?.Value ?? string.Empty)
            .ToArray())
        .ToArray();
}

static string ReadEntry(ZipArchive archive, string name)
{
    var entry = archive.GetEntry(name) ?? throw new InvalidOperationException("Workbook missing ZIP entry: " + name);
    using var reader = new StreamReader(entry.Open());
    return reader.ReadToEnd();
}

static string LocateWorkspaceRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "package.json")) &&
            Directory.Exists(Path.Combine(directory.FullName, "native")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

internal sealed class ExportSmokeOptions
{
    public string? DatabasePath { get; private set; }
    public string? OutputDirectory { get; private set; }
    public string? WorkspaceRoot { get; private set; }
    public string? RealtimeDirectory { get; private set; }
    public string? Building { get; private set; }
    public string? CommunicationState { get; private set; }
    public string? Floor { get; private set; }
    public string? SubArea { get; private set; }
    public string? DeviceName { get; private set; }
    public string? Zuo { get; private set; }
    public string? RealtimeLock { get; private set; }
    public string? AreaType { get; private set; }
    public string? Mode { get; private set; }
    public string? Fan { get; private set; }
    public string? SetTemperature { get; private set; }
    public string? IndoorTemperature { get; private set; }
    public bool AllowEmpty { get; private set; }
    public bool ShowHelp { get; private set; }

    public static ExportSmokeOptions Parse(string[] args)
    {
        var options = new ExportSmokeOptions();
        foreach (var arg in args)
        {
            if (arg is "-h" or "--help")
            {
                options.ShowHelp = true;
                continue;
            }

            var (name, value) = SplitArg(arg);
            switch (name)
            {
                case "--db":
                    options.DatabasePath = EmptyToNull(value);
                    break;
                case "--out":
                    options.OutputDirectory = EmptyToNull(value);
                    break;
                case "--workspace-root":
                    options.WorkspaceRoot = EmptyToNull(value);
                    break;
                case "--realtime-dir":
                    options.RealtimeDirectory = EmptyToNull(value);
                    break;
                case "--building":
                    options.Building = EmptyToNull(value);
                    break;
                case "--comm":
                    options.CommunicationState = EmptyToNull(value);
                    break;
                case "--floor":
                    options.Floor = EmptyToNull(value);
                    break;
                case "--sub-area":
                    options.SubArea = EmptyToNull(value);
                    break;
                case "--device-name":
                case "--search":
                    options.DeviceName = EmptyToNull(value);
                    break;
                case "--zuo":
                    options.Zuo = EmptyToNull(value);
                    break;
                case "--realtime-lock":
                    options.RealtimeLock = EmptyToNull(value);
                    break;
                case "--mode":
                    options.Mode = EmptyToNull(value);
                    break;
                case "--fan":
                    options.Fan = EmptyToNull(value);
                    break;
                case "--set-temp":
                    options.SetTemperature = EmptyToNull(value);
                    break;
                case "--indoor-temp":
                    options.IndoorTemperature = EmptyToNull(value);
                    break;
                case "--area":
                    options.AreaType = EmptyToNull(value);
                    break;
                case "--allow-empty":
                    options.AllowEmpty = true;
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + arg);
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run --project native/tools/EmsScout.ExportSmoke -- --db=<ac.db> --out=<dir> [filters]");
        Console.WriteLine("Filters: --building --zuo --floor --sub-area --device-name --area --comm --mode --fan --set-temp --indoor-temp --realtime-lock");
    }

    private static (string Name, string Value) SplitArg(string arg)
    {
        var index = arg.IndexOf('=', StringComparison.Ordinal);
        return index < 0 ? (arg, string.Empty) : (arg[..index], arg[(index + 1)..]);
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
