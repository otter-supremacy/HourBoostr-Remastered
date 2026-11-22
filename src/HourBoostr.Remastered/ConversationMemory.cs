namespace HourBoostr.Remastered;

public sealed record AiChatTurn(string Role, string Content);
public sealed class ConversationMemory
{
    private readonly Dictionary<string, (DateTimeOffset At, List<AiChatTurn> Turns)> chats = [];
    public AiChatTurn[] Recent(string key, DateTimeOffset now)
    {
        lock (chats)
        {
            foreach (var old in chats.Where(x => now - x.Value.At >= TimeSpan.FromMinutes(20)).Select(x => x.Key).ToArray()) chats.Remove(old);
            return chats.TryGetValue(key, out var chat) ? chat.Turns.ToArray() : [];
        }
    }
    public void Remember(string key, string input, string reply, DateTimeOffset now)
    {
        lock (chats)
        {
            var turns = Recent(key, now).ToList();
            turns.Add(new("user", input[..Math.Min(input.Length, 1000)])); turns.Add(new("assistant", reply[..Math.Min(reply.Length, 350)]));
            if (chats.Count >= 200 && !chats.ContainsKey(key)) chats.Remove(chats.MinBy(x => x.Value.At).Key);
            chats[key] = (now, turns.TakeLast(4).ToList());
        }
    }
    public static bool Repeats(IEnumerable<AiChatTurn> turns, string reply)
        => turns.Any(x => x.Role == "assistant" && string.Equals(x.Content.Trim().TrimEnd('.', '!', '?'), reply.Trim().TrimEnd('.', '!', '?'), StringComparison.OrdinalIgnoreCase));
}
