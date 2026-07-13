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
        var result = await LoadAllRowsAsync(query, cancellationToken).ConfigureAwait(false);
        if (result.Total > ExportLimit)
        {
            throw new InvalidOperationException(
                $"Current export limit is {ExportLimit:N0} rows, but the query returned {result.Total:N0} rows.");
        }

        if (result.Total != result.Rows.Count)
        {
            throw new InvalidOperationException(
                $"Export query count mismatch: result total is {result.Total:N0}, but {result.Rows.Count:N0} rows were loaded.");
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var path = CreateExportPath(outputDirectory, timestamp);
        var sheets = new[]
        {
            new SpreadsheetSheet("devices", DeviceRows(result.Rows)),
        };
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
            },
        };

        values.AddRange(rows.Select(row => new[]
        {
            row.Building,
            row.Zuo ?? string.Empty,
            row.FloorLabel,
            row.PageLabel,
            row.Name,
            row.AreaType,
            row.OperatingStatusText,
            row.Mode,
            row.Fan,
            row.SetTemperature,
            row.IndoorTemperature,
            row.RealtimeLockText,
        }));
        return values;
    }

    private static string CreateExportPath(string outputDirectory, string timestamp)
    {
        for (var suffix = 0; ; suffix++)
        {
            var discriminator = suffix == 0 ? string.Empty : $"_{suffix}";
            var candidate = Path.Combine(outputDirectory, $"数据管理筛选结果_{timestamp}{discriminator}.xlsx");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
