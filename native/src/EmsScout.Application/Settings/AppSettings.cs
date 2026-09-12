namespace EmsScout.Application.Settings;

public sealed class AppSettings
{
    public string EmsUrl { get; set; } = "http://172.29.248.4:8000/ui/#/home/27161";

    public int EdgeCdpPort { get; set; } = 9222;

    public bool CheckLoginBeforeCollection { get; set; } = true;

    public string DataDirectory { get; set; } = "out";

    public string ExportDirectory { get; set; } = "out/data-management-export";

    public bool TrackRecentExports { get; set; } = true;

    public string DefaultCollectionMode { get; set; } = "edge-cdp";

    public string LogLevel { get; set; } = "INFO";

    public bool SaveNdjsonLog { get; set; } = true;

    public int RealtimeBatchSize { get; set; } = 20;

    public int RealtimeReopenEvery { get; set; } = 3;

    public int RealtimeTimeoutMs { get; set; } = 15000;

    public string Theme { get; set; } = "system";

    public bool CompactDataTable { get; set; } = true;

    public bool ReduceMotion { get; set; }

    public string PageTransitionStyle { get; set; } = "fade";

    public bool SaveWindowPlacement { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool LaunchAtLogin { get; set; }

    public bool ShowInSendTo { get; set; }

    public bool CloseToTray { get; set; } = true;

    public WindowPlacementState? WindowPlacement { get; set; }

    public double TemperatureWarningThreshold { get; set; } = 2.0;

    public string OfflineStatusColor { get; set; } = "#808080";

    public string TemperatureWarningColor { get; set; } = "#D97706";

    public string DashboardNormalMode { get; set; } = "制冷";

    public double DashboardTemperatureMin { get; set; } = 22;

    public double DashboardTemperatureMax { get; set; } = 26;

    public AppSettings Clone() => new()
    {
        EmsUrl = EmsUrl,
        EdgeCdpPort = EdgeCdpPort,
        CheckLoginBeforeCollection = CheckLoginBeforeCollection,
        DataDirectory = DataDirectory,
        ExportDirectory = ExportDirectory,
        TrackRecentExports = TrackRecentExports,
        DefaultCollectionMode = DefaultCollectionMode,
        LogLevel = LogLevel,
        SaveNdjsonLog = SaveNdjsonLog,
        RealtimeBatchSize = RealtimeBatchSize,
        RealtimeReopenEvery = RealtimeReopenEvery,
        RealtimeTimeoutMs = RealtimeTimeoutMs,
        Theme = Theme,
        CompactDataTable = CompactDataTable,
        ReduceMotion = ReduceMotion,
        PageTransitionStyle = PageTransitionStyle,
        SaveWindowPlacement = SaveWindowPlacement,
        StartMinimized = StartMinimized,
        LaunchAtLogin = LaunchAtLogin,
        ShowInSendTo = ShowInSendTo,
        CloseToTray = CloseToTray,
        WindowPlacement = WindowPlacement?.Clone(),
        TemperatureWarningThreshold = TemperatureWarningThreshold,
        OfflineStatusColor = OfflineStatusColor,
        TemperatureWarningColor = TemperatureWarningColor,
        DashboardNormalMode = DashboardNormalMode,
        DashboardTemperatureMin = DashboardTemperatureMin,
        DashboardTemperatureMax = DashboardTemperatureMax,
    };
}

public sealed class WindowPlacementState
{
    public int PlacementVersion { get; set; }

    public int Left { get; set; }

    public int Top { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public WindowPlacementState Clone() => new()
    {
        PlacementVersion = PlacementVersion,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
    };
}
