using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmsScout.Application.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;

namespace EmsScout.Desktop.ViewModels;

public sealed partial class DiagnosticsViewModel(
    AppDataPathService pathService) : ObservableObject
{
    private const string RepositoryUrlValue = "https://github.com/osGex0o0II/ems-scout";

    public const string RepositoryOwner = "osGex0o0II";

    public string RepositoryUrl => RepositoryUrlValue;

    private const int PreviewMaxLines = 160;
    private static readonly Regex NativeExportFileNamePattern =
        new(@"^数据管理筛选结果_\d{8}_\d{6}\.xlsx$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private DispatcherQueueTimer? _systemClockTimer;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "诊断信息尚未加载";

    [ObservableProperty]
    public partial string PreviewTitle { get; private set; } = "选择日志文件";

    [ObservableProperty]
    public partial string PreviewText { get; private set; } = "从左侧日志列表选择一个文件后显示末尾内容。";

    [ObservableProperty]
    public partial string CurrentSystemTime { get; private set; } = FormatSystemTime(DateTime.Now);

    [ObservableProperty]
    public partial string SystemLanguage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SystemTimeZone { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WindowsVersion { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ProcessorAndMemory { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedLogCommand))]
    public partial DiagnosticFileRow? SelectedLog { get; set; }

    public ObservableCollection<DiagnosticInfoRow> AppRows { get; } = [];

    public ObservableCollection<DiagnosticInfoRow> DeviceRows { get; } = [];

    public ObservableCollection<DiagnosticInfoRow> PathRows { get; } = [];

    public ObservableCollection<DiagnosticInfoRow> WorkflowRows { get; } = [];

    public ObservableCollection<DiagnosticFileRow> LogFiles { get; } = [];

    public ObservableCollection<DiagnosticFileRow> RecentExports { get; } = [];

    public string WorkspaceRoot => pathService.WorkspaceRoot;

    public string DataDirectory => pathService.DataDirectory;

    public string ExportDirectory => pathService.ExportDirectory;

    public string AuthorText => $"{RepositoryOwner} · Codex";

    public void Load()
    {
        Refresh();
        StartSystemClock();
    }

    public void StopSystemClock()
    {
        if (_systemClockTimer is null)
        {
            return;
        }

        _systemClockTimer.Stop();
        _systemClockTimer = null;
    }

    [RelayCommand]
    private void Refresh()
    {
        AppRows.Clear();
        DeviceRows.Clear();
        PathRows.Clear();
        WorkflowRows.Clear();
        LogFiles.Clear();
        RecentExports.Clear();

        var assembly = Assembly.GetExecutingAssembly();
        var appVersion = GetApplicationVersion(assembly);
        var buildTime = TryGetBuildTime(assembly);

        AppRows.Add(new DiagnosticInfoRow("版本", appVersion, "当前 MSIX 包版本"));
        AppRows.Add(new DiagnosticInfoRow("作者", AuthorText, "项目作者与 AI 协作助手"));
        AppRows.Add(new DiagnosticInfoRow("构建时间", buildTime, "当前运行文件时间"));
        AppRows.Add(new DiagnosticInfoRow("运行环境", $".NET {Environment.Version}", "WinUI 3 / Windows App SDK"));

        SystemLanguage = FormatCulture(CultureInfo.CurrentUICulture);
        SystemTimeZone = FormatTimeZone(TimeZoneInfo.Local);
        DeviceName = Environment.MachineName;
        WindowsVersion = GetWindowsVersion();
        ProcessorAndMemory = GetProcessorAndMemory();
        DeviceRows.Add(new DiagnosticInfoRow("系统语言", SystemLanguage));
        DeviceRows.Add(new DiagnosticInfoRow("系统时区", SystemTimeZone));
        DeviceRows.Add(new DiagnosticInfoRow("设备名称", DeviceName));
        DeviceRows.Add(new DiagnosticInfoRow("Windows 版本", WindowsVersion));
        DeviceRows.Add(new DiagnosticInfoRow("处理器与内存", ProcessorAndMemory));

        StatusText = "软件信息已加载";
    }

    private void StartSystemClock()
    {
        if (_systemClockTimer is not null)
        {
            return;
        }

        _systemClockTimer = _dispatcherQueue.CreateTimer();
        _systemClockTimer.Interval = TimeSpan.FromSeconds(1);
        _systemClockTimer.Tick += (_, _) => CurrentSystemTime = FormatSystemTime(DateTime.Now);
        _systemClockTimer.Start();
    }

    private static string FormatCulture(CultureInfo culture)
    {
        return string.IsNullOrWhiteSpace(culture.Name)
            ? culture.DisplayName
            : $"{culture.Name} · {culture.DisplayName}";
    }

    private static string FormatTimeZone(TimeZoneInfo timeZone)
    {
        var offset = timeZone.GetUtcOffset(DateTimeOffset.Now);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var standardName = string.IsNullOrWhiteSpace(timeZone.StandardName)
            ? timeZone.Id
            : timeZone.StandardName;
        return $"UTC{sign}{offset.Duration():hh\\:mm} · {standardName}";
    }

    private static string GetWindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var productName = key?.GetValue("ProductName") as string;
        var displayVersion = key?.GetValue("DisplayVersion") as string;
        var currentBuild = key?.GetValue("CurrentBuild")?.ToString();
        var ubr = key?.GetValue("UBR")?.ToString();
        var build = string.IsNullOrWhiteSpace(currentBuild)
            ? string.Empty
            : $"{currentBuild}{(string.IsNullOrWhiteSpace(ubr) ? string.Empty : $".{ubr}")}";

        var parts = new[] { productName, displayVersion, string.IsNullOrWhiteSpace(build) ? null : $"10.0.{build}" }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join(" · ", parts);
    }

    private static string GetProcessorAndMemory()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var processor = (key?.GetValue("ProcessorNameString") as string)?.Trim();
        processor = string.IsNullOrWhiteSpace(processor) ? "未知处理器" : processor;
        processor = Regex.Replace(processor, @"\s+w/.*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

        var memory = GetPhysicalMemoryLabel();
        return $"{processor} · {memory}";
    }

    private static string GetPhysicalMemoryLabel()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return "内存未知";
        }

        var gigabytes = Math.Max(1, (int)Math.Round(
            status.TotalPhysicalMemory / Math.Pow(1024, 3),
            MidpointRounding.AwayFromZero));
        return $"{gigabytes} GB";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private static string FormatSystemTime(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss");

    [RelayCommand]
    private void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RepositoryUrl,
                UseShellExecute = true,
            });
            StatusText = "已打开 GitHub 仓库";
        }
        catch (Exception ex)
        {
            StatusText = "无法打开 GitHub 仓库：" + ex.Message;
        }
    }

    [RelayCommand]
    private void CopyRepository()
    {
        CopyToClipboard(RepositoryUrl, "仓库地址已复制");
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        var lines = AppRows.Select(row => $"{row.Label}: {row.Value}（{row.Detail}）").ToList();
        lines.Add($"当前系统时间: {CurrentSystemTime}");
        lines.AddRange(DeviceRows.Select(row => $"{row.Label}: {row.Value}"));
        lines.Add($"仓库: {RepositoryUrl}");
        CopyToClipboard(string.Join(Environment.NewLine, lines), "诊断信息已复制");
    }

    private static string GetApplicationVersion(Assembly assembly)
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (Exception)
        {
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
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

    private void CopyToClipboard(string text, string status)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            StatusText = status;
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ex.Message;
        }
    }
}
