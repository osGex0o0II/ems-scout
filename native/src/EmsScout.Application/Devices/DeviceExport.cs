namespace EmsScout.Application.Devices;

public interface IDeviceExportService
{
    Task<DeviceExportResult> ExportAsync(
        DeviceQuery query,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}

public sealed record DeviceExportResult(
    string Path,
    string FileName,
    string Format,
    int RowCount,
    IReadOnlyList<string> Sheets,
    DeviceFacets Facets);
