using EmsScout.Domain;

namespace EmsScout.Application.Devices;

public static class DashboardAreaGroupMetricRules
{
    public static bool Matches(
        DeviceRecord record,
        string? metric,
        string? normalMode,
        double temperatureMin,
        double temperatureMax)
    {
        return metric?.Trim() switch
        {
            null or "" or "all" => true,
            "online" => IsOnline(record),
            "offline" => record.CommunicationState == DeviceCommunicationState.Offline,
            "mode_abnormal" => IsOnline(record) &&
                                !string.IsNullOrWhiteSpace(record.Mode) &&
                                !string.Equals(record.Mode.Trim(), normalMode?.Trim(), StringComparison.OrdinalIgnoreCase),
            "temperature_abnormal" => IsOnline(record) &&
                                       DeviceTemperatureRules.TryRead(record.SetTemperature, out var temperature) &&
                                       (temperature < temperatureMin || temperature > temperatureMax),
            "lock_on" => HasLock(record, "开启"),
            "lock_off" => HasLock(record, "关闭"),
            _ => false,
        };
    }

    public static bool IsDashboardMetric(string? metric)
    {
        return metric?.Trim() is "online" or "offline" or "mode_abnormal" or
            "temperature_abnormal" or "lock_on" or "lock_off";
    }

    private static bool IsOnline(DeviceRecord record)
    {
        return record.CommunicationState is DeviceCommunicationState.Running or DeviceCommunicationState.Stopped;
    }

    private static bool HasLock(DeviceRecord record, string expected)
    {
        return IsOnline(record) && record.Realtime?.LockStateValid == true &&
               string.Equals(record.Realtime.LockState, expected, StringComparison.OrdinalIgnoreCase);
    }
}
