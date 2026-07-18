using EmsScout.Application.Workflows;

namespace EmsScout.Tests;

public sealed class ApplicationOperationStateTests
{
    [Fact]
    public void CollectionLeaseIsExclusiveAndCanBeReacquiredAfterDispose()
    {
        var state = new ApplicationOperationState();
        var changes = 0;
        state.CollectionTaskStateChanged += (_, _) => changes++;

        using (state.BeginCollectionTask())
        {
            Assert.True(state.IsCollectionTaskRunning);
            Assert.Throws<InvalidOperationException>(() => state.BeginCollectionTask());
        }

        Assert.False(state.IsCollectionTaskRunning);
        using (state.BeginCollectionTask()) Assert.True(state.IsCollectionTaskRunning);
        Assert.False(state.IsCollectionTaskRunning);
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task ConcurrentLeaseAttemptsAllowExactlyOneWinner()
    {
        var state = new ApplicationOperationState();
        using var start = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var ready = new CountdownEvent(16);
        using var attempted = new CountdownEvent(16);
        var attempts = Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            try
            {
                using var lease = state.BeginCollectionTask();
                attempted.Signal();
                release.Wait(TimeSpan.FromSeconds(2));
                return true;
            }
            catch (InvalidOperationException)
            {
                attempted.Signal();
                return false;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        start.Set();
        Assert.True(attempted.Wait(TimeSpan.FromSeconds(2)));
        release.Set();
        var outcomes = await Task.WhenAll(attempts);

        Assert.Single(outcomes, outcome => outcome);
        Assert.False(state.IsCollectionTaskRunning);
    }

    [Fact]
    public void CollectionAndUpdateInstallLeasesExcludeEachOther()
    {
        var state = new ApplicationOperationState();

        using (state.BeginUpdateInstall())
        {
            Assert.True(state.IsUpdateInstallPending);
            Assert.False(state.IsCollectionTaskRunning);
            Assert.Throws<InvalidOperationException>(() => state.BeginCollectionTask());
            Assert.Throws<InvalidOperationException>(() => state.BeginUpdateInstall());
        }

        using (state.BeginCollectionTask())
        {
            Assert.True(state.IsCollectionTaskRunning);
            Assert.False(state.IsUpdateInstallPending);
            Assert.Throws<InvalidOperationException>(() => state.BeginUpdateInstall());
        }

        Assert.False(state.IsCollectionTaskRunning);
        Assert.False(state.IsUpdateInstallPending);
    }
}
