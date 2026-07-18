namespace EmsScout.Application.Workflows;

public sealed class ApplicationOperationState
{
    private const int Idle = 0;
    private const int CollectionOperation = 1;
    private const int UpdateInstallOperation = 2;
    private int _activeOperation;

    public event EventHandler? CollectionTaskStateChanged;

    public event EventHandler? OperationStateChanged;

    public bool IsCollectionTaskRunning => Volatile.Read(ref _activeOperation) == CollectionOperation;

    public bool IsUpdateInstallPending => Volatile.Read(ref _activeOperation) == UpdateInstallOperation;

    public IDisposable BeginCollectionTask()
    {
        if (Interlocked.CompareExchange(ref _activeOperation, CollectionOperation, Idle) != Idle)
        {
            throw new InvalidOperationException("Another application operation is already running.");
        }

        CollectionTaskStateChanged?.Invoke(this, EventArgs.Empty);
        OperationStateChanged?.Invoke(this, EventArgs.Empty);
        return new OperationLease(this, CollectionOperation);
    }

    public IDisposable BeginUpdateInstall()
    {
        if (Interlocked.CompareExchange(ref _activeOperation, UpdateInstallOperation, Idle) != Idle)
        {
            throw new InvalidOperationException("Another application operation is already running.");
        }

        OperationStateChanged?.Invoke(this, EventArgs.Empty);
        return new OperationLease(this, UpdateInstallOperation);
    }

    private void EndOperation(int operation)
    {
        if (Interlocked.CompareExchange(ref _activeOperation, Idle, operation) != operation)
        {
            return;
        }

        if (operation == CollectionOperation)
        {
            CollectionTaskStateChanged?.Invoke(this, EventArgs.Empty);
        }

        OperationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class OperationLease(ApplicationOperationState owner, int operation) : IDisposable
    {
        private ApplicationOperationState? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndOperation(operation);
    }
}
