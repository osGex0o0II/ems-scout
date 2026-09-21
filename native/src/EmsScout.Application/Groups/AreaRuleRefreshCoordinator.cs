namespace EmsScout.Application.Groups;

public sealed record AreaRuleMatchWorkItem(string Key, AreaGroupRuleRecord Rule);

/// <summary>
/// Coalesces rule edits into a cancellable, dirty-row-only match request.
/// </summary>
public sealed class AreaRuleRefreshCoordinator : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<AreaGroupRuleRecord>, CancellationToken, Task<IReadOnlyList<int>>> _countAsync;
    private readonly Action<IReadOnlyList<AreaRuleMatchWorkItem>, IReadOnlyList<int>> _publish;
    private readonly TimeSpan _debounce;
    private readonly SynchronizationContext? _publishContext;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Dictionary<string, AreaRuleMatchWorkItem> _pending = new(StringComparer.Ordinal);
    private CancellationTokenSource? _activeCancellation;
    private Task? _activeTask;
    private long _generation;
    private bool _disposed;

    public AreaRuleRefreshCoordinator(
        Func<IReadOnlyList<AreaGroupRuleRecord>, CancellationToken, Task<IReadOnlyList<int>>> countAsync,
        Action<IReadOnlyList<AreaRuleMatchWorkItem>, IReadOnlyList<int>> publish,
        TimeSpan? debounce = null,
        SynchronizationContext? publishContext = null)
    {
        _countAsync = countAsync ?? throw new ArgumentNullException(nameof(countAsync));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(200);
        if (_debounce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));

        _publishContext = publishContext ?? SynchronizationContext.Current;
    }

    public Task QueueAsync(
        IEnumerable<AreaRuleMatchWorkItem> dirtyItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dirtyItems);
        var items = dirtyItems.ToArray();
        if (items.Length == 0)
            return Task.CompletedTask;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var item in items)
                _pending[item.Key] = item;

            _generation++;
            _activeCancellation?.Cancel();
            _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            var generation = _generation;
            var activeCancellation = _activeCancellation;
            _activeTask = RunAsync(generation, activeCancellation);
            return _activeTask;
        }
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _generation++;
            _pending.Clear();
            _activeCancellation?.Cancel();
        }
    }

    private async Task RunAsync(long generation, CancellationTokenSource activeCancellation)
    {
        try
        {
            await Task.Delay(_debounce, activeCancellation.Token).ConfigureAwait(true);
            activeCancellation.Token.ThrowIfCancellationRequested();

            AreaRuleMatchWorkItem[] items;
            lock (_gate)
            {
                if (_disposed || generation != _generation)
                    return;

                items = _pending.Values.ToArray();
            }

            if (items.Length == 0)
                return;

            var records = items.Select(item => item.Rule).ToArray();
            var counts = await _countAsync(records, activeCancellation.Token).ConfigureAwait(true);
            activeCancellation.Token.ThrowIfCancellationRequested();
            if (counts.Count != items.Length)
                throw new InvalidOperationException("规则匹配数返回数量不一致。");

            await PublishOnCapturedContextAsync(generation, items, counts)
                .WaitAsync(activeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (activeCancellation.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, activeCancellation))
                {
                    _activeCancellation = null;
                    _activeTask = null;
                }
            }
            activeCancellation.Dispose();
        }
    }

    private Task PublishOnCapturedContextAsync(
        long generation,
        IReadOnlyList<AreaRuleMatchWorkItem> items,
        IReadOnlyList<int> counts)
    {
        if (_publishContext is null || ReferenceEquals(SynchronizationContext.Current, _publishContext))
        {
            PublishIfCurrent(generation, items, counts);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _publishContext.Post(
            static state =>
            {
                var payload = (PublishPayload)state!;
                try
                {
                    payload.Coordinator.PublishIfCurrent(payload.Generation, payload.Items, payload.Counts);
                    payload.Completion.SetResult();
                }
                catch (Exception ex)
                {
                    payload.Completion.SetException(ex);
                }
            },
            new PublishPayload(this, generation, items, counts, completion));
        return completion.Task;
    }

    private void PublishIfCurrent(
        long generation,
        IReadOnlyList<AreaRuleMatchWorkItem> items,
        IReadOnlyList<int> counts)
    {
        lock (_gate)
        {
            if (!_disposed && generation == _generation)
            {
                _pending.Clear();
                _publish(items, counts);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? activeTask;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _pending.Clear();
            _lifetime.Cancel();
            _activeCancellation?.Cancel();
            activeTask = _activeTask;
        }

        if (activeTask is not null)
            await activeTask.ConfigureAwait(false);

        lock (_gate)
        {
            _activeCancellation = null;
            _lifetime.Dispose();
        }
    }

    private sealed record PublishPayload(
        AreaRuleRefreshCoordinator Coordinator,
        long Generation,
        IReadOnlyList<AreaRuleMatchWorkItem> Items,
        IReadOnlyList<int> Counts,
        TaskCompletionSource Completion);
}
