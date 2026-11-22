namespace HourBoostr.Remastered;

// Persist before sending: an interrupted or uncertain delivery must not be replayed.
public sealed class FriendWelcomeGate(DataStore store, string accountId)
{
    private readonly object gate = new();
    public bool TryClaim(ulong recipient, bool acceptedAutomatically, AccountSettings config)
    {
        if (!acceptedAutomatically || recipient == 0 || !config.Enabled || !config.AutoAcceptFriendRequests || !config.FriendWelcomeEnabled || string.IsNullOrWhiteSpace(config.FriendWelcomeMessage)) return false;
        lock (gate)
        {
            var name = $"friend-welcomes-{accountId}.json";
            var claimed = store.Read<HashSet<ulong>>(name) ?? [];
            if (!claimed.Add(recipient)) return false;
            store.Write(name, claimed);
            return true;
        }
    }
}
