using HourBoostr.Remastered;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class FriendWelcomeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hbr-welcome-" + Guid.NewGuid().ToString("N"));
    private static AccountSettings Enabled() => new() { AutoAcceptFriendRequests = true, FriendWelcomeEnabled = true, FriendWelcomeMessage = "Hello!" };

    [Fact]
    public void RequiresConfirmedAutomaticAcceptanceAndBothOptions()
    {
        var store = new DataStore(root); var account = Enabled(); var gate = new FriendWelcomeGate(store, account.Id);
        Assert.False(gate.TryClaim(2, false, account));
        account.AutoAcceptFriendRequests = false; Assert.False(gate.TryClaim(2, true, account));
        account.AutoAcceptFriendRequests = true; account.FriendWelcomeEnabled = false; Assert.False(gate.TryClaim(2, true, account));
        account.FriendWelcomeEnabled = true; account.Enabled = false; Assert.False(gate.TryClaim(2, true, account));
        account.Enabled = true; Assert.False(gate.TryClaim(0, true, account));
        Assert.True(gate.TryClaim(2, true, account));
    }

    [Fact]
    public void ClaimSurvivesRestartAndIsIsolatedPerAccount()
    {
        var store = new DataStore(root); var first = store.Add("First"); first.AutoAcceptFriendRequests = true; first.FriendWelcomeEnabled = true;
        Assert.True(new FriendWelcomeGate(store, first.Id).TryClaim(2, true, first));
        Assert.False(new FriendWelcomeGate(new DataStore(root), first.Id).TryClaim(2, true, first));
        var second = Enabled(); Assert.True(new FriendWelcomeGate(store, second.Id).TryClaim(2, true, second));
        Assert.DoesNotContain(first.FriendWelcomeMessage, File.ReadAllText(store.Resolve($"friend-welcomes-{first.Id}.json")));
        store.Remove(first.Id); Assert.False(File.Exists(store.Resolve($"friend-welcomes-{first.Id}.json")));
    }

    [Fact]
    public void ConcurrentDuplicateCallbacksClaimOnlyOnce()
    {
        var config = Enabled(); var gate = new FriendWelcomeGate(new DataStore(root), config.Id); var count = 0;
        Parallel.For(0, 30, _ => { if (gate.TryClaim(2, true, config)) Interlocked.Increment(ref count); });
        Assert.Equal(1, count);
    }

    [Fact]
    public void ValidatesAndPersistsCustomMessageWithoutEnablingOldAccounts()
    {
        var store = new DataStore(root); var account = store.Add("Test"); Assert.False(account.FriendWelcomeEnabled);
        account.FriendWelcomeEnabled = true; account.FriendWelcomeMessage = "  "; Assert.Throws<ArgumentException>(account.Validate);
        account.FriendWelcomeMessage = new string('x', 501); Assert.Throws<ArgumentException>(account.Validate);
        account.FriendWelcomeMessage = "Thanks for adding me!"; store.Save(account);
        var saved = new DataStore(root).Account(account.Id)!; Assert.True(saved.FriendWelcomeEnabled); Assert.Equal(account.FriendWelcomeMessage, saved.FriendWelcomeMessage);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
