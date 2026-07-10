namespace EmsScout.Application.Devices;

public sealed record DeviceFilterOptions(
    IReadOnlyList<DeviceFilterOption> Buildings,
    IReadOnlyList<DeviceFilterOption> CommunicationStates,
    IReadOnlyList<DeviceFilterOption> Floors,
    IReadOnlyList<DeviceFilterOption> SubAreas,
    IReadOnlyList<DeviceFilterOption> PageNames,
    IReadOnlyList<DeviceFilterOption> DeviceNames,
    IReadOnlyList<DeviceFilterOption> Zuos,
    IReadOnlyList<DeviceFilterOption> Modes,
    IReadOnlyList<DeviceFilterOption> Fans,
    IReadOnlyList<DeviceFilterOption> SetTemperatures,
    IReadOnlyList<DeviceFilterOption> IndoorTemperatures,
    IReadOnlyList<DeviceFilterOption> Tags,
    IReadOnlyList<DeviceFilterOption>? RealtimePowers = null,
    IReadOnlyList<DeviceFilterOption>? RealtimeModes = null,
    IReadOnlyList<DeviceFilterOption>? RealtimeFans = null,
    IReadOnlyList<DeviceFilterOption>? RealtimeLocks = null,
    IReadOnlyList<DeviceFilterOption>? RealtimeSystemTypes = null);
