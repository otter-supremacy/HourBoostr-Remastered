namespace HourBoostr.Remastered;

internal sealed class PrivateReplyQueue
{
    private sealed class Conversation
    {
        public Queue<string> Pending { get; } = new();
        public (string Text, bool Unified, long Tick)? LastIncoming { get; set; }
    }

    private readonly Dictionary<ulong, Conversation> conversations = [];

    // One worker per friend. Keep the last few lines of a burst instead of sending a reply to every line.
    public Task OfferAsync(ulong sender, string text, bool unified, Func<string, CancellationToken, Task> reply, CancellationToken token)
    {
        if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        text = text.Trim();
        text = text[..Math.Min(1000, text.Length)];
        Conversation conversation;
        lock (conversations)
        {
            var running = conversations.TryGetValue(sender, out var current);
            if (!running && conversations.Count >= 64) return Task.CompletedTask;
            conversation = current ?? new();
            var now = Environment.TickCount64;
            if (conversation.LastIncoming is { } previous && previous.Unified != unified && previous.Text == text && now - previous.Tick < 1000)
                return Task.CompletedTask;
            conversation.LastIncoming = (text, unified, now);
            if (conversation.Pending.Count >= 3) conversation.Pending.Dequeue();
            conversation.Pending.Enqueue(text);
            if (running) return Task.CompletedTask;
            conversations.Add(sender, conversation);
        }
        return DrainAsync(sender, conversation, reply, token);
    }

    private async Task DrainAsync(ulong sender, Conversation conversation, Func<string, CancellationToken, Task> reply, CancellationToken token)
    {
        var removed = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(250, token);
                string text;
                lock (conversations)
                {
                    if (conversation.Pending.Count == 0)
                    {
                        conversations.Remove(sender);
                        removed = true;
                        return;
                    }
                    var combined = string.Join('\n', conversation.Pending);
                    text = combined[Math.Max(0, combined.Length - 1000)..];
                    conversation.Pending.Clear();
                }
                await reply(text, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (!removed)
            {
                lock (conversations) conversations.Remove(sender);
            }
        }
    }
}
