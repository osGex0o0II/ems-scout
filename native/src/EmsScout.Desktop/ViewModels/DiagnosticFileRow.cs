namespace EmsScout.Desktop.ViewModels;

public sealed class DiagnosticFileRow(FileInfo file, string rootPath, string kind)
{
    public string Name { get; } = file.Name;

    public string Kind { get; } = kind;

    public string FullPath { get; } = file.FullName;

    public string RelativePath { get; } = MakeRelativePath(rootPath, file.FullName);

    public string Size { get; } = FormatSize(file.Length);

    public string ModifiedAt { get; } = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");

    public string Summary => $"{Kind} | {Size} | {ModifiedAt}";

    private static string MakeRelativePath(string rootPath, string fullPath)
    {
        try
        {
            return Path.GetRelativePath(rootPath, fullPath);
        }
        catch (ArgumentException)
        {
            return fullPath;
        }
    }

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
