namespace EmsScout.Application.Settings;

public static class PathSafety
{
    public static string ResolveDirectory(string workspaceRoot, string configuredPath, bool allowExternal = false)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) throw new InvalidOperationException("目录不能为空。");

        var workspace = CanonicalizeExistingParent(Path.GetFullPath(workspaceRoot));
        var candidate = Path.GetFullPath(configuredPath, workspace);
        if (File.Exists(candidate)) throw new InvalidOperationException("配置路径必须是目录，不能是文件。");

        var canonical = CanonicalizeExistingParent(candidate);
        if (IsSystemDirectory(canonical)) throw new InvalidOperationException("禁止使用 Windows 或 Program Files 系统目录。");
        if (!allowExternal && !IsWithin(workspace, canonical))
        {
            throw new InvalidOperationException("目录必须位于工作区内；使用外部目录前需显式确认。");
        }
        if (Path.GetPathRoot(canonical)?.Equals(canonical, StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("禁止将磁盘根目录作为数据目录。");
        }

        return canonical;
    }

    private static bool IsWithin(string parent, string candidate)
    {
        var root = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemDirectory(string path)
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return (!string.IsNullOrWhiteSpace(systemRoot) && IsWithin(systemRoot, path)) ||
            (!string.IsNullOrWhiteSpace(programFiles) && IsWithin(programFiles, path)) ||
            (!string.IsNullOrWhiteSpace(programFilesX86) && IsWithin(programFilesX86, path));
    }

    private static string CanonicalizeExistingParent(string path)
    {
        var full = Path.GetFullPath(path);
        var pending = new Stack<string>();
        var current = new DirectoryInfo(full);
        while (current is not null && !current.Exists)
        {
            pending.Push(current.Name);
            current = current.Parent;
        }

        if (current is null) return full;
        var resolved = current.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current.FullName;
        while (pending.Count > 0) resolved = Path.Combine(resolved, pending.Pop());
        return Path.GetFullPath(resolved);
    }
}
