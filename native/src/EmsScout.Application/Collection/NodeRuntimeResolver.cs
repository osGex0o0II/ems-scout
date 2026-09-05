namespace EmsScout.Application.Collection;

public static class NodeRuntimeResolver
{
    public static string Resolve()
    {
        var overridePath = Environment.GetEnvironmentVariable("EMS_NODE_RUNTIME");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return ValidateAbsoluteExecutable(overridePath, "EMS_NODE_RUNTIME");
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
        };
        var hit = candidates.FirstOrDefault(File.Exists);
        return hit is null
            ? throw new InvalidOperationException("未找到受信任的 Node.js 运行时；请安装 Node.js 或设置 EMS_NODE_RUNTIME 为绝对路径。")
            : Path.GetFullPath(hit);
    }

    private static string ValidateAbsoluteExecutable(string value, string variableName)
    {
        var path = value.Trim();
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), "node.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new InvalidOperationException($"{variableName} 必须是存在的 Node.js exe 绝对路径。实际值：{path}");
        }

        return Path.GetFullPath(path);
    }
}
