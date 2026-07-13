using EmsScout.Domain;

namespace EmsScout.Application.Devices;

public static class DeviceHealthRules
{
    public static DeviceHealthAssessment Evaluate(DeviceRecord record)
    {
        var issues = new List<string>();
        var hasTemperatureIssue = false;

        if (string.IsNullOrWhiteSpace(record.Name) || record.Name == "0-0001-KT")
        {
            issues.Add("设备名占位");
        }

        if (record.EffectiveCommunicationState == DeviceCommunicationState.Offline)
        {
            issues.Add("通讯离线");
        }
        else if (record.EffectiveCommunicationState == DeviceCommunicationState.Unknown)
        {
            issues.Add("通讯未知");
        }

        if (IsTemperatureAbnormal(record.IndoorTemperature) || IsTemperatureAbnormal(record.SetTemperature))
        {
            issues.Add("温度异常");
            hasTemperatureIssue = true;
        }
        else if (record.EffectiveCommunicationState != DeviceCommunicationState.Offline &&
                 (IsTemperatureMissing(record.IndoorTemperature) || IsTemperatureMissing(record.SetTemperature)))
        {
            issues.Add("温度缺失");
            hasTemperatureIssue = true;
        }

        if (issues.Count == 0)
        {
            return new DeviceHealthAssessment("normal", "正常", "无排查项", NeedsReview: false, HasTemperatureIssue: false);
        }

        var status = record.EffectiveCommunicationState == DeviceCommunicationState.Offline
            ? "offline"
            : "attention";
        return new DeviceHealthAssessment(status, "需排查", string.Join("、", issues), NeedsReview: true, hasTemperatureIssue);
    }

    public static bool MatchesQuickFilter(DeviceRecord record, string? quickFilter)
    {
        return quickFilter?.Trim() switch
        {
            null or "" or "all" => true,
            "normal" => !record.Health.NeedsReview,
            "needs_review" => record.Health.NeedsReview,
            "offline" => record.EffectiveCommunicationState == DeviceCommunicationState.Offline,
            "temp_abnormal" => record.Health.HasTemperatureIssue,
            "on" => record.EffectiveCommunicationState == DeviceCommunicationState.Running,
            "off" => record.EffectiveCommunicationState == DeviceCommunicationState.Stopped,
            "public" => record.AreaType == DeviceAreaClassifier.PublicArea,
            "private" => record.AreaType == DeviceAreaClassifier.PrivateArea,
            "unknown" => record.EffectiveCommunicationState == DeviceCommunicationState.Unknown,
            "locked" => record.RealtimeLocked,
            "points_incomplete" => record.Realtime is null || !record.RealtimePointsComplete,
            "realtime_missing" => record.Realtime is null,
            _ => true,
        };
    }

    private static bool IsTemperatureAbnormal(string value)
    {
        return DeviceTemperatureRules.TryRead(value, out _) && DeviceTemperatureRules.IsAbnormalOrMissing(value);
    }

    private static bool IsTemperatureMissing(string value)
    {
        return !DeviceTemperatureRules.TryRead(value, out _);
    }
}
