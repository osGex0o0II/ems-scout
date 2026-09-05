using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Settings;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class DiagnosticsViewModel(
    AppDataPathService pathService) : ObservableObject
{
    private const int PreviewMaxLines = 160;
    private static readonly Regex NativeExportFileNamePattern =
        new(@"^数据管理筛选结果_\d{8}_\d{6}\.xlsx$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "诊断信息尚未加载";

    [ObservableProperty]
    public partial string PreviewTitle { get; private set; } = "选择日志文件";

    [ObservableProperty]
    public partial string PreviewText { get; private set; } = "从左侧日志列表选择一个文件后显示末尾内容。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedLogCommand))]
    public partial DiagnosticFileRow? SelectedLog { get; set; }

    public ObservableCollection<DiagnosticInfoRow> AppRows { get; } = [];

    public ObservableCollection<DiagnosticInfoRow> PathRows { get; } = [];

    public ObservableCollection<DiagnosticInfoRow> WorkflowRows { get; } = [];

    public ObservableCollection<DiagnosticFileRow> LogFiles { get; } = [];

    public ObservableCollection<DiagnosticFileRow> RecentExports { get; } = [];

    public string WorkspaceRoot => pathService.WorkspaceRoot;

    public string DataDirectory => pathService.DataDirectory;

    public string ExportDirectory => pathService.ExportDirectory;

    public void Load()
    {
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        AppRows.Clear();
        PathRows.Clear();
        WorkflowRows.Clear();
        LogFiles.Clear();
        RecentExports.Clear();

        var assembly = Assembly.GetExecutingAssembly();
        var appVersion = assembly.GetName().Version?.ToString() ?? "unknown";
        var buildTime = TryGetBuildTime(assembly);

        AppRows.Add(new DiagnosticInfoRow("软件", "EMS 空调控制台", "空调设备采集与数据管理"));
        AppRows.Add(new DiagnosticInfoRow("版本", appVersion, "当前程序集版本"));
        AppRows.Add(new DiagnosticInfoRow("作者", "EMS Scout Team", "项目维护团队"));
        AppRows.Add(new DiagnosticInfoRow("构建时间", buildTime, "当前运行文件时间"));
        AppRows.Add(new DiagnosticInfoRow("运行环境", $".NET {Environment.Version}", "WinUI 3 / Windows App SDK"));

        StatusText = "软件信息已加载";
    }

    private static string TryGetBuildTime(Assembly assembly)
    {
        try
        {
            var location = assembly.Location;
            return !string.IsNullOrWhiteSpace(location) && File.Exists(location)
                ? File.GetLastWriteTime(location).ToString("yyyy-MM-dd HH:mm:ss")
                : "随发布包提供";
        }
        catch
        {
            return "随发布包提供";
        }
    }

    partial void OnSelectedLogChanged(DiagnosticFileRow? value)
    {
        LoadSelectedLogPreview();
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedLog))]
    private void OpenSelectedLog()
    {
        if (SelectedLog is null)
        {
            return;
        }

        OpenFileInExplorer(SelectedLog.FullPath);
    }

    [RelayCommand]
    private void OpenWorkspace()
    {
        OpenDirectory(pathService.WorkspaceRoot);
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        OpenDirectory(DataDirectory);
    }

    [RelayCommand]
    private void OpenExportDirectory()
    {
        OpenDirectory(ExportDirectory);
    }

    public void OpenRecentExport(DiagnosticFileRow? row)
    {
        if (row is null)
        {
            return;
        }

        OpenFileInExplorer(row.FullPath);
    }

    private bool CanOpenSelectedLog() => SelectedLog is not null && File.Exists(SelectedLog.FullPath);

    private void AddPathRow(string label, string path, bool exists)
    {
        PathRows.Add(new DiagnosticInfoRow(label, path, exists ? "存在" : "缺失"));
    }

    private IEnumerable<DiagnosticFileRow> EnumerateLogs()
    {
        var rows = new List<DiagnosticFileRow>();
        AddLogFiles(rows, DataDirectory, "enum_*.log", "枚举日志");
        AddLogFiles(rows, DataDirectory, "panel_task_*.log", "任务日志");
        AddLogFiles(rows, pathService.WorkspaceRoot, "out\\native-*.log", "原生运行日志");
        AddLogFiles(rows, pathService.WorkspaceRoot, "logs\\*.log", "桌面日志");

        return rows
            .GroupBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(row => File.GetLastWriteTime(row.FullPath))
            .Take(50);
    }

    private IEnumerable<DiagnosticFileRow> EnumerateRecentExports()
    {
        if (!Directory.Exists(ExportDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(ExportDirectory, "数据管理筛选结果_*.xlsx", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => NativeExportFileNamePattern.IsMatch(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(20)
            .Select(file => new DiagnosticFileRow(file, ExportDirectory, "筛选 Excel"));
    }

    private void AddLogFiles(List<DiagnosticFileRow> rows, string root, string pattern, string kind)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var directory = root;
        var filePattern = pattern;
        var separatorIndex = pattern.LastIndexOf('\\');
        if (separatorIndex >= 0)
        {
            directory = Path.Combine(root, pattern[..separatorIndex]);
            filePattern = pattern[(separatorIndex + 1)..];
        }

        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, filePattern, SearchOption.TopDirectoryOnly))
        {
            rows.Add(new DiagnosticFileRow(new FileInfo(path), pathService.WorkspaceRoot, kind));
        }
    }

    private void LoadSelectedLogPreview()
    {
        if (SelectedLog is null)
        {
            PreviewTitle = "选择日志文件";
            PreviewText = "从左侧日志列表选择一个文件后显示末尾内容。";
            return;
        }

        PreviewTitle = SelectedLog.RelativePath;
        try
        {
            PreviewText = ReadTail(SelectedLog.FullPath, PreviewMaxLines);
        }
        catch (IOException ex)
        {
            PreviewText = "无法读取日志：" + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            PreviewText = "没有权限读取日志：" + ex.Message;
        }
    }

    private static string ReadTail(string path, int maxLines)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new Queue<string>(maxLines);
        while (reader.ReadLine() is { } line)
        {
            if (lines.Count >= maxLines)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line);
        }

        return lines.Count == 0
            ? "日志为空。"
            : string.Join(Environment.NewLine, lines);
    }

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private static void OpenFileInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { "/select,", path },
            UseShellExecute = true,
        });
    }
}
