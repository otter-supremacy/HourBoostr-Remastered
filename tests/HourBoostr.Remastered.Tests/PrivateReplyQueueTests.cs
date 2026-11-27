using System.Collections.Concurrent;
using HourBoostr.Remastered;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class PrivateReplyQueueTests
{
    [Fact]
    public async Task MessagesDuringPendingReplyAreBatchedAndDuplicateTransportDoesNotSendAgain()
    {
        var queue = new PrivateReplyQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = new ConcurrentQueue<string>();
        async Task Reply(string text, CancellationToken token)
        {
            replies.Enqueue(text);
            if (text == "hi") { started.SetResult(); await release.Task.WaitAsync(token); }
        }

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var worker = queue.OfferAsync(1, "hi", true, Reply, stop.Token);
        await started.Task.WaitAsync(stop.Token);
        await queue.OfferAsync(1, "hi", false, Reply, stop.Token);
        await queue.OfferAsync(1, "which model are you?", true, Reply, stop.Token);
        await queue.OfferAsync(1, "second line", true, Reply, stop.Token);
        var other = queue.OfferAsync(2, "another friend", true, Reply, stop.Token);
        await other.WaitAsync(stop.Token);
        release.SetResult();
        await worker.WaitAsync(stop.Token);
        Assert.Equal(["hi", "another friend", "which model are you?\nsecond line"], replies.ToArray());
    }
}
