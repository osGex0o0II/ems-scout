namespace EmsScout.Domain;

public static class DeviceCommunicationStateParser
{
    public static DeviceCommunicationState Parse(string? value)
    {
        return value?.Trim() switch
        {
            "开机" => DeviceCommunicationState.Running,
            "关机" => DeviceCommunicationState.Stopped,
            "离线" => DeviceCommunicationState.Offline,
            "ON" => DeviceCommunicationState.Running,
            "OFF" => DeviceCommunicationState.Stopped,
            _ => DeviceCommunicationState.Unknown
        };
    }
}
