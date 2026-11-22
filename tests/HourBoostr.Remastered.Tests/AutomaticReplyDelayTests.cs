using System.Diagnostics;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class AutomaticReplyDelayTests
{
    [Fact]
    public async Task WaitsThreeSecondsBeforeSendingOnce()
    {
        var elapsed = Stopwatch.StartNew();
        var sends = 0;
        Assert.True(await AutomaticReplyDelay.SendAsync(() => true, () =>
        {
            Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(3));
            sends++;
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task SessionCancellationPreventsPendingSend()
    {
        using var stop = new CancellationTokenSource();
        var sends = 0;
        var pending = AutomaticReplyDelay.SendAsync(() => true, () => { sends++; return Task.CompletedTask; }, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task DisablingRepliesDuringTheWaitPreventsSend()
    {
        var enabled = true;
        var sends = 0;
        var pending = AutomaticReplyDelay.SendAsync(() => enabled, () => { sends++; return Task.CompletedTask; }, CancellationToken.None);
        enabled = false;
        Assert.False(await pending);
        Assert.Equal(0, sends);
    }
}
