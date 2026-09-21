using EmsScout.Application.Groups;

namespace EmsScout.Tests;

public sealed class AreaRuleRefreshCoordinatorTests
{
    [Fact]
    public async Task QueuesWithinDebounceWindowCoalesceToOneDirtyRuleBatch()
    {
        var calls = new List<IReadOnlyList<AreaGroupRuleRecord>>();
        var gate = new object();
        await using var coordinator = new AreaRuleRefreshCoordinator(
            async (rules, cancellationToken) =>
            {
                lock (gate) calls.Add(rules);
                await Task.Yield();
                return rules.Select(_ => 7).ToArray();
            },
            (_, _) => { },
            TimeSpan.FromMilliseconds(200));

        var rule = CreateRule(1);
        Task? final = null;
        for (var index = 0; index < 10; index++)
            final = coordinator.QueueAsync([new AreaRuleMatchWorkItem("row-1", rule)]);

        await final!;

        Assert.Single(calls);
        Assert.Single(calls[0]);
        Assert.Equal(rule, calls[0][0]);
    }

    [Fact]
    public async Task QueuesKeepUnpublishedDirtyRowsAndNeverPublishCancelledResults()
    {
        var published = new List<string>();
        var callCount = 0;
        await using var coordinator = new AreaRuleRefreshCoordinator(
            async (rules, cancellationToken) =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Delay(30, cancellationToken);
                return rules.Select(_ => 3).ToArray();
            },
            (items, _) => published.AddRange(items.Select(item => item.Key)),
            TimeSpan.FromMilliseconds(20));

        var first = coordinator.QueueAsync([new AreaRuleMatchWorkItem("row-1", CreateRule(1))]);
        await Task.Delay(25);
        var second = coordinator.QueueAsync([new AreaRuleMatchWorkItem("row-2", CreateRule(2))]);
        await second;
        await first;

        Assert.True(callCount >= 1);
        Assert.Equal(["row-1", "row-2"], published);
    }

    [Fact]
    public async Task SupersededResultIsDiscardedWhenRepositoryIgnoresCancellation()
    {
        var published = new List<int>();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        await using var coordinator = new AreaRuleRefreshCoordinator(
            async (rules, _) =>
            {
                var invocation = Interlocked.Increment(ref call);
                if (invocation == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
                return rules.Select(_ => invocation == 1 ? 1 : 5).ToArray();
            },
            (_, counts) => published.AddRange(counts),
            TimeSpan.Zero);

        var first = coordinator.QueueAsync([new AreaRuleMatchWorkItem("row", CreateRule(1))]);
        await firstStarted.Task;
        var second = coordinator.QueueAsync([new AreaRuleMatchWorkItem("row", CreateRule(2))]);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal([5], published);
    }

    [Fact]
    public async Task DisposeCancelsPendingWorkWithoutPublishing()
    {
        var published = 0;
        var coordinator = new AreaRuleRefreshCoordinator(
            async (_, cancellationToken) =>
            {
                await Task.Delay(100, cancellationToken);
                return new[] { 1 };
            },
            (_, _) => Interlocked.Increment(ref published),
            TimeSpan.FromMilliseconds(10));

        var work = coordinator.QueueAsync([new AreaRuleMatchWorkItem("row-1", CreateRule(1))]);
        await coordinator.DisposeAsync();
        await work;

        Assert.Equal(0, published);
    }

    [Fact]
    public async Task QueueCompletesOnlyAfterCapturedSynchronizationContextPublishes()
    {
        var context = new ManualSynchronizationContext();
        var published = 0;
        await using var coordinator = new AreaRuleRefreshCoordinator(
            (rules, _) => Task.FromResult<IReadOnlyList<int>>(rules.Select(_ => 1).ToArray()),
            (_, _) => published++,
            TimeSpan.Zero,
            context);

        var work = coordinator.QueueAsync([new AreaRuleMatchWorkItem("row-1", CreateRule(1))]);
        await context.Posted;

        Assert.False(work.IsCompleted);
        Assert.Equal(0, published);

        context.ExecutePending();
        await work;

        Assert.Equal(1, published);
    }

    [Fact]
    public async Task DisposeDoesNotWaitForAnUnpumpedPublishAndQueuedCallbackStaysStale()
    {
        var context = new ManualSynchronizationContext();
        var published = 0;
        var coordinator = new AreaRuleRefreshCoordinator(
            (rules, _) => Task.FromResult<IReadOnlyList<int>>(rules.Select(_ => 1).ToArray()),
            (_, _) => published++,
            TimeSpan.Zero,
            context);

        var work = coordinator.QueueAsync([new AreaRuleMatchWorkItem("row-1", CreateRule(1))]);
        await context.Posted;
        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await work;

        context.ExecutePending();
        Assert.Equal(0, published);
    }

    private static AreaGroupRuleRecord CreateRule(long id) => new(
        id,
        1,
        (int)id,
        "1号",
        "-",
        "-",
        0,
        AreaGroupRuleNormalizer.Include,
        ["关键词"],
        string.Empty);

    private sealed class ManualSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            _posted.TrySetResult();
        }

        public void ExecutePending()
        {
            while (_callbacks.TryDequeue(out var work))
                work.Callback(work.State);
        }
    }
}
