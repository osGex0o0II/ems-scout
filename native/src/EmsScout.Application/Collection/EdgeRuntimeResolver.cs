namespace EmsScout.Application.Collection;

public static class EdgeRuntimeResolver
{
    public static string Resolve()
    {
        var overridePath = Environment.GetEnvironmentVariable("EDGE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath)) return Validate(overridePath, "EDGE_PATH");

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe"),
        };
        var hit = candidates.FirstOrDefault(File.Exists);
        return hit is null
            ? throw new InvalidOperationException("未找到 Microsoft Edge；请安装 Edge 或设置 EDGE_PATH 为绝对路径。")
            : Path.GetFullPath(hit);
    }

    private static string Validate(string value, string variableName)
    {
        var path = value.Trim();
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new InvalidOperationException($"{variableName} 必须是存在的 msedge.exe 绝对路径。实际值：{path}");
        }

        return Path.GetFullPath(path);
    }
}
