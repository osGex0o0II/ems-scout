using EmsScout.Domain;
using EmsScout.Application.Watch;

namespace EmsScout.Application.Devices;

public sealed record DeviceRecord(
    long Id,
    string Building,
    double? Floor,
    string FloorLabel,
    string SubArea,
    double? X,
    double? Y,
    string PageName,
    string Name,
    string Layout,
    string SwitchState,
    string Mode,
    string IndoorTemperature,
    string SetTemperature,
    string Fan,
    string Indicator,
    string CommunicationText,
    DeviceCommunicationState CommunicationState,
    RealtimeDetailRecord? Realtime = null,
    string RealtimeMatchKind = "",
    string? AreaTypeOverride = null,
    string? Zuo = null,
    string? ZuoSource = null,
    string? Note = null,
    IReadOnlyList<string>? Tags = null,
    long? MatchOverrideId = null,
    string? MatchOverrideAction = null,
    string? MatchOverrideNote = null,
    bool IsVirtual = false,
    DeviceWatchState? Watch = null)
{
    public string Location => $"{Building} / {FloorLabel} / {SubArea}";

    public string PageLabel => DevicePageNameFormatter.Format(PageName);

    public string AreaType => string.IsNullOrWhiteSpace(AreaTypeOverride)
        ? DeviceAreaClassifier.Classify(Name, Layout)
        : AreaTypeOverride;

    public DeviceHealthAssessment Health => DeviceHealthRules.Evaluate(this);

    public bool HasRealtime => Realtime is not null;

    public string RealtimeMatchLabel => Realtime is null
        ? "无实时详情"
        : RealtimeMatchKind switch
        {
            "manual" => "手动匹配",
            "virtual" => "虚拟纳管",
            "name" => "同名匹配",
            "classify" => "人工分类",
            _ => "已匹配",
        };

    public bool RealtimePointsComplete => Realtime?.PointsComplete == true;

    public bool RealtimeLocked => Realtime?.LockState == "开启";

    public string CommunicationStatusText => string.IsNullOrWhiteSpace(CommunicationText)
        ? "未知"
        : CommunicationText;

    public string RealtimeLockText => Realtime is null
        ? "无实时数据"
        : string.IsNullOrWhiteSpace(Realtime.LockState) ? "未知" : Realtime.LockState;

    public IReadOnlyList<string> TagList => Tags ?? [];

    public bool HasManualOverride => MatchOverrideId is not null;

    public DeviceWatchState WatchState => Watch ?? DeviceWatchState.Unwatched;

    public bool IsWatched => WatchState.IsWatched;

    public bool IsWatchAbnormal => WatchState.IsAbnormal;

    public string TemperatureText => string.IsNullOrWhiteSpace(IndoorTemperature)
        ? "--"
        : $"{IndoorTemperature} C";
}
