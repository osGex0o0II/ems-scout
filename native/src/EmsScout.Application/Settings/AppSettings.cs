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

    public string Theme { get; set; } = "system";

    public bool CompactDataTable { get; set; } = true;

    public bool ReduceMotion { get; set; }

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
        Theme = Theme,
        CompactDataTable = CompactDataTable,
        ReduceMotion = ReduceMotion,
    };
}
