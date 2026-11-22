namespace HourBoostr.Remastered;

// Bounded per-room state. Reserve before sending; delivery failures are never retried.
public sealed class GroupReplyGate
{
    private readonly Dictionary<(ulong, ulong), (int Next, DateTimeOffset At)> rooms = [];
    private readonly HashSet<(ulong, ulong, uint, uint)> seen = [];
    private readonly Queue<(ulong, ulong, uint, uint)> order = [];
    public string? Reserve(GroupReplySettings settings, ulong group, ulong room, ulong sender, ulong self,
        uint timestamp, uint ordinal, DateTimeOffset now)
    {
        if (!settings.Enabled || group == 0 || room == 0 || sender == 0 || sender == self || settings.Messages.Count != 3 || settings.Messages.Any(string.IsNullOrWhiteSpace)) return null;
        // Old/replayed notifications must never trigger replies after reconnect.
        if (timestamp == 0 || Math.Abs(now.ToUnixTimeSeconds() - timestamp) > 60) return null;
        lock (rooms)
        {
            var key = (group, room, timestamp, ordinal);
            if (!seen.Add(key)) return null;
            order.Enqueue(key);
            while (order.Count > 4096) seen.Remove(order.Dequeue());
            var state = rooms.GetValueOrDefault((group, room));
            if (now - state.At < TimeSpan.FromSeconds(settings.CooldownSeconds)) return null;
            if (rooms.Count >= 1024 && !rooms.ContainsKey((group, room))) return null;
            rooms[(group, room)] = ((state.Next + 1) % 3, now);
            return settings.Messages[state.Next];
        }
    }
}
