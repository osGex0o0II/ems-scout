using EmsScout.Domain;

namespace EmsScout.Application.Devices;

public static class DeviceOperatingStatusResolver
{
    public static string ResolveText(string? communicationText, string? switchState)
    {
        var communication = string.IsNullOrWhiteSpace(communicationText) ? "未知" : communicationText.Trim();
        var deviceSwitch = (switchState ?? string.Empty).Trim().ToUpperInvariant();
        if (communication == "离线") return "离线";
        if (communication == "开机") return "开机";
        if (communication == "关机") return "关机";
        if (deviceSwitch == "ON") return "开机";
        if (deviceSwitch == "OFF") return "关机";
        return "未知";
    }

    public static DeviceCommunicationState ResolveState(string? communicationText, string? switchState) =>
        DeviceCommunicationStateParser.Parse(ResolveText(communicationText, switchState));
}
