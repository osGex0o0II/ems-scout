namespace EmsScout.Desktop.ViewModels;

public sealed class RecentExportRow(FileInfo file, string exportDirectory)
{
    public string Name { get; } = file.Name;

    public string FullPath { get; } = file.FullName;

    public string RelativePath { get; } = Path.GetRelativePath(exportDirectory, file.FullName);

    public string Size { get; } = FormatSize(file.Length);

    public string ModifiedAt { get; } = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");

    public string Summary => $"{Size} | {ModifiedAt}";

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.0} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024d:0.0} KB";
        }

        return $"{bytes:N0} B";
    }
}
