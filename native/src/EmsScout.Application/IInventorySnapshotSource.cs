using EmsScout.Domain;

namespace EmsScout.Application;

public interface IInventorySnapshotSource
{
    Task<InventorySnapshot> LoadAsync(CancellationToken cancellationToken = default);
}
