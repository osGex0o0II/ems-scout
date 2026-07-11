namespace EmsScout.Application.Workflows;

public sealed class ApplicationOperationState
{
    private int _activeCollectionTasks;

    public event EventHandler? CollectionTaskStateChanged;

    public bool IsCollectionTaskRunning => Volatile.Read(ref _activeCollectionTasks) > 0;

    public IDisposable BeginCollectionTask()
    {
        if (Interlocked.CompareExchange(ref _activeCollectionTasks, 1, 0) != 0)
        {
            throw new InvalidOperationException("Another collection task is already running.");
        }

        CollectionTaskStateChanged?.Invoke(this, EventArgs.Empty);
        return new CollectionTaskLease(this);
    }

    private void EndCollectionTask()
    {
        if (Interlocked.Exchange(ref _activeCollectionTasks, 0) == 1)
        {
            CollectionTaskStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class CollectionTaskLease(ApplicationOperationState owner) : IDisposable
    {
        private ApplicationOperationState? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndCollectionTask();
    }
}
