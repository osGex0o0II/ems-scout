namespace EmsScout.Application.Devices;

public interface IDeviceAnnotationService
{
    Task SaveNoteAsync(
        DeviceAnnotationKey key,
        string note,
        CancellationToken cancellationToken = default);

    Task AddTagAsync(
        DeviceAnnotationKey key,
        string tag,
        CancellationToken cancellationToken = default);

    Task DeleteTagAsync(
        DeviceAnnotationKey key,
        string tag,
        CancellationToken cancellationToken = default);

    Task<RealtimeMatchOverride?> SaveRealtimeOverrideAsync(
        RealtimeOverrideEdit edit,
        CancellationToken cancellationToken = default);
}

public sealed record DeviceAnnotationKey(
    string Building,
    string CardName);

public sealed record RealtimeOverrideEdit(
    string Building,
    string DevId,
    string FloorLabel,
    string SubArea,
    string PageName,
    string RealtimeName,
    string? Action = null,
    long? TargetCardId = null,
    string? ZuoOverride = null,
    string? AreaTypeOverride = null,
    string? Note = null);
