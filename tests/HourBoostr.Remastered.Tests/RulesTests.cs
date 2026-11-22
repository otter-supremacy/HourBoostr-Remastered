using HourBoostr.Remastered;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class RulesTests
{
    private static ScheduleSettings Overnight() => new() { Enabled = true, TimeZone = "UTC", Start = "22:00", End = "03:00", Days = [DayOfWeek.Friday] };
    [Theory]
    [InlineData("2026-10-23T23:00:00Z", true)]
    [InlineData("2026-10-24T02:59:00Z", true)]
    [InlineData("2026-10-24T03:00:00Z", false)]
    [InlineData("2026-10-24T23:00:00Z", false)]
    public void OvernightSchedulesBelongToStartingDay(string now, bool expected) => Assert.Equal(expected, ScheduleRule.Allows(Overnight(), DateTimeOffset.Parse(now)));
    [Fact]
    public void ScheduleHonorsDaylightSavingTransition()
    {
        var schedule = new ScheduleSettings { Enabled = true, TimeZone = "America/New_York", Start = "01:00", End = "02:00", Days = [DayOfWeek.Sunday] };
        Assert.True(ScheduleRule.Allows(schedule, DateTimeOffset.Parse("2026-11-01T05:30:00Z")));
        Assert.True(ScheduleRule.Allows(schedule, DateTimeOffset.Parse("2026-11-01T06:30:00Z")));
        Assert.False(ScheduleRule.Allows(schedule, DateTimeOffset.Parse("2026-11-01T07:00:00Z")));
    }
    [Fact]
    public void SelectionRequiresOwnershipRespectsExclusionsTargetsAndLimit()
    {
        var games = Enumerable.Range(1, 50).ToDictionary(i => (uint)i, i => new LibraryGame { AppId = (uint)i, EstimatedTotalSeconds = i * 3600 });
        var settings = new AccountSettings { PreferredAppId = 10, SelectionMode = "manual", SelectedAppIds = Enumerable.Range(1, 60).Select(i => (uint)i).ToList(), ExcludedAppIds = [2], TargetHours = new() { [3] = 2 } };
        var chosen = GameSelection.Choose(settings, games, [], false);
        Assert.Equal(32, chosen.Count); Assert.Equal(10u, chosen[0]); Assert.Equal(chosen.Count, chosen.Distinct().Count());
        Assert.DoesNotContain(2u, chosen); Assert.DoesNotContain(3u, chosen); Assert.All(chosen, id => Assert.True(games.ContainsKey(id)));
    }
    [Fact]
    public void RandomSelectionKeepsGroupUntilRotation()
    {
        var games = Enumerable.Range(1, 40).ToDictionary(i => (uint)i, i => new LibraryGame { AppId = (uint)i });
        var settings = new AccountSettings { PreferredAppId = 10, SelectionMode = "random", RandomCount = 10 };
        var first = GameSelection.Choose(settings, games, [], false);
        Assert.Equal(10, first.Count); Assert.Equal(first, GameSelection.Choose(settings, games, first, false));
        settings.ExcludedAppIds = [first[1]];
        Assert.DoesNotContain(first[1], GameSelection.Choose(settings, games, first, false));
    }
    [Fact]
    public void RotationUsesTheSelectedPool()
    {
        var games = Enumerable.Range(1, 40).ToDictionary(i => (uint)i, i => new LibraryGame { AppId = (uint)i });
        var config = new AccountSettings { SelectionMode = "rotate", RandomCount = 2, SelectedAppIds = [1, 2, 3] };
        var chosen = GameSelection.Choose(config, games, [], true);
        Assert.Equal(2, chosen.Count); Assert.All(chosen, id => Assert.Contains(id, config.SelectedAppIds));
    }
    [Fact]
    public void RepliesDoNotLoopAndCooldownIsPerFriend()
    {
        var settings = new AfkSettings { Enabled = true }; var gate = new ReplyGate(); var now = DateTimeOffset.UtcNow;
        Assert.False(gate.TryReserve(1, 1, true, false, settings, now));
        Assert.False(gate.TryReserve(2, 1, true, true, settings, now));
        Assert.False(gate.TryReserve(2, 1, false, false, settings, now));
        Assert.True(gate.TryReserve(2, 1, true, false, settings, now));
        Assert.False(gate.TryReserve(2, 1, true, false, settings, now.AddMinutes(9)));
        Assert.True(gate.TryReserve(3, 1, true, false, settings, now.AddMinutes(1)));
        Assert.True(gate.TryReserve(2, 1, true, false, settings, now.AddMinutes(10)));
    }
    [Fact]
    public void QuietHoursAndExcludedFriendsBlockReplies()
    {
        var settings = new AfkSettings { Enabled = true, ExcludedFriends = ["76561198000000001"], QuietHours = Overnight() };
        var gate = new ReplyGate();
        Assert.False(gate.TryReserve(2, 1, true, false, settings, DateTimeOffset.Parse("2026-10-23T23:00:00Z")));
        Assert.False(gate.TryReserve(76561198000000001, 1, true, false, settings, DateTimeOffset.Parse("2026-10-24T12:00:00Z")));
    }
    [Fact]
    public void HealthDoesNotTreatNormalPauseOrAuthAsAStall()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(HealthRule.NeedsRecovery(new() { LoggedIn = true, PlayingElsewhere = true, LastSteamResponse = now.AddMinutes(-5) }, now));
        Assert.False(HealthRule.NeedsRecovery(new() { LoggedIn = false, State = "auth_required", LastSteamResponse = now.AddHours(-1) }, now));
        Assert.True(HealthRule.NeedsRecovery(new() { LoggedIn = true, ConsecutiveProbeFailures = 3, LastSteamResponse = now.AddMinutes(-13) }, now));
        Assert.False(HealthRule.NeedsRecovery(new() { LoggedIn = true, Connected = true, ConsecutiveProbeFailures = 20, LastSteamResponse = now.AddHours(-1) }, now));
    }
    [Theory]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("1.0.0", "1.0.0-preview.1", true)]
    [InlineData("1.0.0-preview.10", "1.0.0-preview.2", true)]
    [InlineData("1.0.0-preview.1", "1.0.0", false)]
    [InlineData("v1.0.0+sha", "1.0.0", false)]
    public void ReleaseOrdering(string candidate, string current, bool expected) => Assert.Equal(expected, ReleaseTools.IsNewer(candidate, current));
    [Fact]
    public void InvalidConfigurationIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new AccountSettings { RandomCount = 33 }.Validate());
        Assert.Throws<ArgumentException>(() => new AccountSettings { TargetHours = new() { [10] = double.NaN } }.Validate());
        Assert.Throws<ArgumentException>(() => new AccountSettings { Schedule = new() { Start = "24:99" } }.Validate());
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["serve", "--listen", "0.0.0.0"]));
    }
}
