namespace EmsScout.Domain;

public sealed record InventorySnapshot(
    string SourcePath,
    DateTimeOffset SourceUpdatedAt,
    IReadOnlyList<AirConditionerCard> Cards);
