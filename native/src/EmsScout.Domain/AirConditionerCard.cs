namespace EmsScout.Domain;

public sealed record AirConditionerCard(
    string Building,
    string SubArea,
    double? Floor,
    string Page,
    string Name,
    string SwitchState,
    string Mode,
    double? IndoorTemperature,
    double? SetTemperature,
    string Fan,
    string Indicator,
    DeviceCommunicationState CommunicationState);
