namespace HourBoostr.Remastered;

public static class AutomaticReplyDelay
{
    public const int Seconds = 3;

    public static async Task<bool> SendAsync(Func<bool> canSend, Func<Task> send, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!canSend()) return false;
        await Task.Delay(TimeSpan.FromSeconds(Seconds), token);
        token.ThrowIfCancellationRequested();
        if (!canSend()) return false;
        await send();
        return true;
    }
}
