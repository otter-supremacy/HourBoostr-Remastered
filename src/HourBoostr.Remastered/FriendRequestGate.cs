using SteamKit2;

namespace HourBoostr.Remastered;

public sealed class FriendRequestGate
{
    private readonly Dictionary<ulong, DateTimeOffset> attempted = new();
    private DateTimeOffset nextAttempt;
    public bool TryReserve(bool enabled, ulong requester, ulong self, EAccountType accountType, EFriendRelationship relationship, DateTimeOffset now)
    {
        if (!enabled || requester == 0 || requester == self || accountType != EAccountType.Individual || relationship != EFriendRelationship.RequestRecipient) return false;
        lock (attempted)
        {
            foreach (var expired in attempted.Where(x => now - x.Value >= TimeSpan.FromMinutes(10)).Select(x => x.Key).ToArray()) attempted.Remove(expired);
            if (now < nextAttempt || attempted.ContainsKey(requester)) return false;
            attempted[requester] = now; nextAttempt = now.AddSeconds(2); return true;
        }
    }
}
