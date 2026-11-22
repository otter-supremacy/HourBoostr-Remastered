using HourBoostr.Remastered;
using SteamKit2;
using Xunit;
namespace HourBoostr.Remastered.Tests;

public sealed class ProfileDisplayTests
{
    private readonly DateTimeOffset start = DateTimeOffset.Parse("2026-10-31T10:00:00Z");
    private static Dictionary<uint, LibraryGame> Library() => Enumerable.Range(1,32).Select(i => (uint)i).Append(570u).ToDictionary(i => i, i => new LibraryGame { AppId = i, Name = "Game " + i });
    private static List<uint> Chosen() => Enumerable.Range(1,32).Select(i => (uint)i).ToList();
    [Fact]
    public void DefaultDoesNotChangeExistingIdling()
    {
        var plan = ProfileDisplayRule.Choose(new(), Library(), Chosen(), start, true);
        Assert.Equal(Chosen(), plan.SteamGames); Assert.Null(plan.CustomText); Assert.Null(plan.NextChange);
        Assert.Equal(32, ProfileDisplayRule.Packet(plan).Body.games_played.Count);
    }
    [Fact]
    public void RealGamesRotateExactlyHourlyAndRespectCapacity()
    {
        var config = new AccountSettings { ProfileDisplay = new() { Mode = "rotate", AppIds = [10,570], AnchorUtc = start } };
        var first = ProfileDisplayRule.Choose(config, Library(), Chosen(), start.AddMinutes(59), true);
        Assert.Equal(10u, first.SteamGames[0]); Assert.Equal(start.AddHours(1), first.NextChange);
        var second = ProfileDisplayRule.Choose(config, Library(), Chosen(), start.AddHours(1), true);
        Assert.Equal(570u, second.SteamGames[0]); Assert.Equal(32, second.SteamGames.Count); Assert.DoesNotContain(32u, second.SteamGames);
        var persisted = JsonDefaults.Clone(config);
        Assert.Equal(10u, ProfileDisplayRule.Choose(persisted, Library(), Chosen(), start.AddHours(2), true).SteamGames[0]);
    }
    [Fact]
    public void CustomUsesShortcutPacketAndNeverCountsAsSteamGame()
    {
        var config = new AccountSettings { ProfileDisplay = new() { Mode = "custom", CustomText = "AFK custom text" } };
        var plan = ProfileDisplayRule.Choose(config, Library(), Chosen(), start, true);
        Assert.Equal(31, plan.SteamGames.Count);
        var packet = ProfileDisplayRule.Packet(plan).Body.games_played;
        Assert.Equal(32, packet.Count); Assert.Equal("AFK custom text", packet[0].game_extra_info);
        Assert.Equal(GameID.GameType.Shortcut, new GameID(packet[0].game_id).AppType);
        Assert.Null(plan.NextChange);
    }
    [Fact]
    public void MixedCyclesAndPausedPlansAreEmpty()
    {
        var config = new AccountSettings { ProfileDisplay = new() { Mode = "mixed", AppIds = [10], CustomText = "Custom", AnchorUtc = start } };
        Assert.Null(ProfileDisplayRule.Choose(config, Library(), Chosen(), start, true).CustomText);
        Assert.Equal("Custom", ProfileDisplayRule.Choose(config, Library(), Chosen(), start.AddHours(1), true).CustomText);
        var paused = ProfileDisplayRule.Choose(config, Library(), Chosen(), start.AddHours(1), false);
        Assert.Empty(paused.SteamGames); Assert.Null(paused.CustomText);
    }
    [Fact]
    public void ExclusionsAndCompletedTargetsAndUnknownGamesAreSkipped()
    {
        var config = new AccountSettings { ExcludedAppIds = [10], TargetHours = new() { [11] = 1 }, ProfileDisplay = new() { Mode = "rotate", AppIds = [9999,10,11,570], AnchorUtc = start } };
        var library = Library(); library[11].EstimatedTotalSeconds = 3600;
        Assert.Equal(570u, ProfileDisplayRule.Choose(config, library, Chosen(), start, true).SteamGames[0]);
    }
    [Fact]
    public void SettingsValidateAndLegacyDefaultsRemainOff()
    {
        Assert.Equal("default", new AccountSettings().ProfileDisplay.Mode);
        Assert.Throws<ArgumentException>(() => new ProfileDisplaySettings { Mode = "custom" }.Validate());
        Assert.Throws<ArgumentException>(() => new ProfileDisplaySettings { Mode = "rotate" }.Validate());
        Assert.Throws<ArgumentException>(() => new ProfileDisplaySettings { CustomText = "bad\nline" }.Validate());
        Assert.Throws<ArgumentException>(() => new ProfileDisplaySettings { CustomText = new string('界', 50) }.Validate());
    }
}
