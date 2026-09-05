using EmsScout.Application.Devices;
using EmsScout.Infrastructure.Exporting;
using System.Globalization;

namespace EmsScout.Infrastructure.Sqlite;

public sealed class SqliteDeviceExportService(IDeviceReadRepository repository) : IDeviceExportService
{
    private const int ExportLimit = 50000;

    public async Task<DeviceExportResult> ExportAsync(
        DeviceQuery query,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (query.RunId is not null)
        {
            throw new InvalidOperationException("历史批次为只读预览，不能导出为当前数据管理筛选结果。");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
        }

        Directory.CreateDirectory(outputDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(outputDirectory, $"数据管理筛选结果_{timestamp}.xlsx");
        return await ExportToFileAsync(query, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceExportResult> ExportToFileAsync(
        DeviceQuery query,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (query.RunId is not null)
        {
            throw new InvalidOperationException("历史批次为只读预览，不能导出为当前数据管理筛选结果。");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path is required.", nameof(outputPath));
        }

        var path = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Excel output path must use the .xlsx extension.", nameof(outputPath));
        }

        var outputDirectory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Excel output path must include a directory.", nameof(outputPath));
        }

        Directory.CreateDirectory(outputDirectory);
        var result = await LoadAllRowsAsync(query, cancellationToken).ConfigureAwait(false);
        if (result.Total > ExportLimit)
        {
            throw new InvalidOperationException(
                $"Current export limit is {ExportLimit:N0} rows, but the query returned {result.Total:N0} rows.");
        }

        var sheets = BuildSheets(result.Rows);
        SpreadsheetWorkbookWriter.Write(path, sheets);

        return new DeviceExportResult(
            Path: path,
            FileName: Path.GetFileName(path),
            Format: "xlsx",
            RowCount: result.Rows.Count,
            Sheets: sheets.Select(sheet => sheet.Name).ToArray(),
            Facets: result.Facets);
    }

    private async Task<DeviceListResult> LoadAllRowsAsync(
        DeviceQuery query,
        CancellationToken cancellationToken)
    {
        return await repository.SearchAsync(
            query with { Limit = ExportLimit, Offset = 0 },
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<IReadOnlyList<string>> DeviceRows(IReadOnlyList<DeviceRecord> rows)
    {
        var values = new List<IReadOnlyList<string>>
        {
            new[]
            {
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
                "采集时间",
            },
        };

        values.AddRange(rows.Select(row => new[]
        {
            row.Building,
            ExportSeat(row),
            row.FloorLabel,
            ExportPage(row.PageName),
            row.Name,
            row.AreaType,
            row.CommunicationStatusText,
            row.Mode,
            row.Fan,
            row.SetTemperature,
            row.IndoorTemperature,
            row.RealtimeLockText,
            ExportCollectedAt(row.CollectedAt),
        }));
        return values;
    }

    private static string ExportSeat(DeviceRecord row)
    {
        if (!string.IsNullOrWhiteSpace(row.PageSection))
        {
            return row.PageSection.Trim();
        }

        return string.IsNullOrWhiteSpace(row.Zuo) ? "-" : row.Zuo.Trim();
    }

    private static string ExportPage(string? value)
    {
        var pageName = DevicePageNameFormatter.NormalizeValue(value);
        var separator = pageName.LastIndexOf("/", StringComparison.Ordinal);
        if (separator >= 0 && separator < pageName.Length - 1)
        {
            pageName = pageName[(separator + 1)..];
        }

        return pageName switch
        {
            "default" or "BM" or "一页" => "第1页",
            "二页" => "第2页",
            "三页" => "第3页",
            "四页" => "第4页",
            "五页" => "第5页",
            "六页" => "第6页",
            _ => pageName,
        };
    }

    private static string ExportCollectedAt(DateTimeOffset? value)
    {
        return value is null
            ? "-"
            : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<SpreadsheetSheet> BuildSheets(IReadOnlyList<DeviceRecord> rows)
    {
        var sheets = new List<SpreadsheetSheet>
        {
            new("全部设备", DeviceRows(rows), DeviceColumnWidths),
        };

        foreach (var group in rows
                     .GroupBy(row => string.IsNullOrWhiteSpace(row.Building) ? "未分楼栋" : row.Building.Trim())
                     .OrderBy(group => BuildingOrder(group.Key)))
        {
            var name = group.Key == "未分楼栋" ? group.Key : group.Key + "楼";
            sheets.Add(new SpreadsheetSheet(name, DeviceRows(group.ToArray()), DeviceColumnWidths));
        }

        return sheets;
    }

    private static int BuildingOrder(string building)
    {
        return building switch
        {
            "1号" => 1,
            "2号" => 2,
            "3号" => 3,
            "4号" => 4,
            "5号" => 5,
            "6号" => 6,
            _ => int.MaxValue,
        };
    }

    private static readonly double[] DeviceColumnWidths =
    [
        10, 9, 9, 11, 24, 11, 13, 12, 10, 13, 13, 15, 20,
    ];
}
