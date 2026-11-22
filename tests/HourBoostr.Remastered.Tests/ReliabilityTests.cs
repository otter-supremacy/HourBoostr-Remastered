using HourBoostr.Remastered;
using Xunit;
namespace HourBoostr.Remastered.Tests;
public sealed class ReliabilityTests
{
    [Fact]
    public void StaleGamesRpcAloneDoesNotForceReconnect()
    {
        var now = DateTimeOffset.UtcNow;
        var view = new AccountView { LoggedIn = true, LastSteamResponse = now.AddMinutes(-20) };
        Assert.False(HealthRule.NeedsRecovery(view, now));
        view.ConsecutiveProbeFailures = 3; Assert.True(HealthRule.NeedsRecovery(view, now));
        view.LastSteamResponse = now.AddSeconds(-10); Assert.False(HealthRule.NeedsRecovery(view, now));
    }
    [Fact]
    public void BackoffHasBoundedFailureStreak()
    {
        Assert.Equal(10, ReconnectPolicy.DelaySeconds(1)); Assert.Equal(160, ReconnectPolicy.DelaySeconds(100));
    }
    [Fact]
    public void AutomaticRepliesAreFilteredWithoutBlockingDifferentManualText()
    {
        var gate = new AutomaticReplyTracker(); var now = DateTimeOffset.UtcNow;
        gate.Record(1, 2, "automatic reply", now);
        Assert.True(gate.Matches(1, 2, "automatic reply", now));
        Assert.False(gate.Matches(1, 2, "manually typed message", now));
        Assert.False(gate.Matches(3, 2, "automatic reply", now));
        Assert.False(gate.Matches(1, 2, "automatic reply", now.AddMinutes(3)));
    }
}
