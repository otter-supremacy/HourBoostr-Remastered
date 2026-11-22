using System.Security.Cryptography;
using System.Text;

namespace HourBoostr.Remastered;

public sealed class AutomaticReplyTracker
{
    private readonly Dictionary<(ulong, ulong), (string Hash, DateTimeOffset Until)> sent = [];
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public void Record(ulong sender, ulong recipient, string text, DateTimeOffset now)
    {
        lock (sent)
        {
            foreach (var key in sent.Where(x => x.Value.Until <= now).Select(x => x.Key).ToArray()) sent.Remove(key);
            if (sent.Count >= 4096 && !sent.ContainsKey((sender, recipient))) sent.Remove(sent.MinBy(x => x.Value.Until).Key);
            sent[(sender, recipient)] = (Hash(text), now.AddMinutes(2));
        }
    }
    public bool Matches(ulong sender, ulong recipient, string text, DateTimeOffset now)
    {
        lock (sent) return sent.TryGetValue((sender, recipient), out var entry) && entry.Until > now && entry.Hash == Hash(text);
    }
}
