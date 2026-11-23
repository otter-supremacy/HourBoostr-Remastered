using System.Diagnostics;

namespace HourBoostr.Remastered;

public static class AutomaticReplyDelay
{
    public const int Seconds = 3;

    public static async Task<bool> SendAsync(Func<bool> canSend, Func<Task> send, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!canSend()) return false;
        var minimumDelay = TimeSpan.FromSeconds(Seconds);
        var started = Stopwatch.GetTimestamp();
        var remaining = minimumDelay;
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), token);
            remaining = minimumDelay - Stopwatch.GetElapsedTime(started);
        } while (remaining > TimeSpan.Zero);
        token.ThrowIfCancellationRequested();
        if (!canSend()) return false;
        await send();
        return true;
    }
}
