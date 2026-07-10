using EmsScout.Domain;

namespace EmsScout.Application.Devices;

public sealed record DeviceFacets(
    int Total,
    int Running,
    int Stopped,
    int Offline,
    int Unknown,
    int PublicArea,
    int PrivateArea,
    int TemperatureIssues,
    int NeedsReview,
    int RealtimeRows = 0,
    int RealtimeMatched = 0,
    int RealtimeMissing = 0,
    int RealtimeUnmatched = 0,
    int RealtimeLocked = 0,
    int RealtimePointsComplete = 0,
    int RealtimePointsIncomplete = 0,
    int RealtimeInvalid = 0,
    int VirtualManaged = 0,
    int ManualOverrides = 0,
    int Watched = 0,
    int WatchAbnormal = 0,
    int WatchNormal = 0)
{
    public static DeviceFacets From(
        IEnumerable<DeviceRecord> records,
        int realtimeRows = 0,
        int realtimeUnmatched = 0)
    {
        var rows = records.ToList();
        return new DeviceFacets(
            Total: rows.Count,
            Running: rows.Count(row => row.CommunicationState == DeviceCommunicationState.Running),
            Stopped: rows.Count(row => row.CommunicationState == DeviceCommunicationState.Stopped),
            Offline: rows.Count(row => row.CommunicationState == DeviceCommunicationState.Offline),
            Unknown: rows.Count(row => row.CommunicationState == DeviceCommunicationState.Unknown),
            PublicArea: rows.Count(row => row.AreaType == DeviceAreaClassifier.PublicArea),
            PrivateArea: rows.Count(row => row.AreaType == DeviceAreaClassifier.PrivateArea),
            TemperatureIssues: rows.Count(row => row.Health.HasTemperatureIssue),
            NeedsReview: rows.Count(row => row.Health.NeedsReview),
            RealtimeRows: realtimeRows,
            RealtimeMatched: rows.Count(row => row.Realtime is not null),
            RealtimeMissing: rows.Count(row => row.Realtime is null),
            RealtimeUnmatched: realtimeUnmatched,
            RealtimeLocked: rows.Count(row => row.RealtimeLocked),
            RealtimePointsComplete: rows.Count(row => row.RealtimePointsComplete),
            RealtimePointsIncomplete: rows.Count(row => row.Realtime is null || !row.RealtimePointsComplete),
            RealtimeInvalid: rows.Count(row => row.Realtime?.IsInvalid == true),
            VirtualManaged: rows.Count(row => row.IsVirtual),
            ManualOverrides: rows.Count(row => row.HasManualOverride),
            Watched: rows.Count(row => row.IsWatched),
            WatchAbnormal: rows.Count(row => row.IsWatchAbnormal),
            WatchNormal: rows.Count(row => row.IsWatched && !row.IsWatchAbnormal));
    }
}
