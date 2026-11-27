namespace HourBoostr.Remastered;

// Bounded per-room state. Reserve before sending; delivery failures are never retried.
public sealed class GroupReplyGate
{
    private readonly Dictionary<(ulong, ulong), (int Next, DateTimeOffset At)> rooms = [];
    private readonly HashSet<(ulong, ulong, uint, uint)> seen = [];
    private readonly Queue<(ulong, ulong, uint, uint)> order = [];
    public string? NextPreset(GroupReplySettings settings, ulong group, ulong room)
    {
        lock (rooms)
        {
            if (settings.Messages.Count != 3 || !rooms.TryGetValue((group, room), out var state)) return null;
            rooms[(group, room)] = ((state.Next + 1) % 3, state.At);
            return settings.Messages[state.Next];
        }
    }
    public string? Reserve(GroupReplySettings settings, ulong group, ulong room, ulong sender, ulong self,
        uint timestamp, uint ordinal, DateTimeOffset now, bool useAi = false)
    {
        if (!settings.Enabled || group == 0 || room == 0 || sender == 0 || sender == self || (!useAi && (settings.Messages.Count != 3 || settings.Messages.Any(string.IsNullOrWhiteSpace)))) return null;
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
            rooms[(group, room)] = (useAi ? state.Next : (state.Next + 1) % 3, now);
            return useAi ? string.Empty : settings.Messages[state.Next];
        }
    }
}

internal sealed class GroupPresetGuard(DataStore store, string accountId)
{
    private readonly string file = $"group-fallbacks-{accountId}.json";
    private readonly Dictionary<string, DateTimeOffset> blocked = store.Read<Dictionary<string, DateTimeOffset>>($"group-fallbacks-{accountId}.json") ?? [];

    // A room key is always groupId:roomId. Keep the account timestamp even after the room recovers.
    private const string LastPreset = "last-account-preset";
    public bool Claim(string room, DateTimeOffset now)
    {
        lock (blocked)
        {
            var latest = blocked.TryGetValue(LastPreset, out var stamp) ? stamp : blocked.Values.DefaultIfEmpty().Max();
            if (now - latest < TimeSpan.FromMinutes(10) || blocked.ContainsKey(room) || blocked.Count >= 1025) return false;
            blocked[LastPreset] = now;
            blocked.Add(room, now);
            store.Write(file, blocked);
            return true;
        }
    }

    public void ConfirmAi(string room, DateTimeOffset started)
    {
        lock (blocked)
        {
            if (!blocked.TryGetValue(room, out var at) || at >= started) return;
            blocked.Remove(room);
            store.Write(file, blocked);
        }
    }
}
