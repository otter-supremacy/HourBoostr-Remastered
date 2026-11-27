using HourBoostr.Remastered;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class GroupReplyTests
{
    private readonly GroupReplySettings settings = new() { Enabled = true, Messages = ["one", "two", "three"], CooldownSeconds = 1 };
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private string? Send(GroupReplyGate gate, int second, ulong room = 2, ulong sender = 3) =>
        gate.Reserve(settings, 1, room, sender, 4, (uint)now.AddSeconds(second).ToUnixTimeSeconds(), 1, now.AddSeconds(second));

    [Fact]
    public void GroupFallbackWaitsForNewerConfirmedAiEvenAfterRestart()
    {
        var path = Directory.CreateTempSubdirectory();
        try
        {
            var store = new DataStore(path.FullName);
            var guard = new GroupPresetGuard(store, "cow");
            Assert.True(guard.Claim("1:2", now));
            guard = new GroupPresetGuard(store, "cow");
            guard.ConfirmAi("1:2", now.AddSeconds(-1));
            Assert.False(guard.Claim("1:2", now.AddMinutes(6)));
            Assert.False(guard.Claim("1:3", now.AddSeconds(30)));
            guard.ConfirmAi("1:2", now.AddMinutes(7));
            guard = new GroupPresetGuard(store, "cow");
            Assert.False(guard.Claim("1:2", now.AddMinutes(8)));
            Assert.True(guard.Claim("1:2", now.AddMinutes(10)));
            Assert.False(guard.Claim("1:3", now.AddMinutes(11)));
            Assert.True(guard.Claim("1:3", now.AddMinutes(20)));
            Assert.False(guard.Claim("1:2", now.AddHours(2)));
        }
        finally { path.Delete(true); }
    }

    [Fact]
    public void GroupMentionUsesSenderIdAndProtectsTheNameBoundary()
    {
        Assert.Equal("[mention=42]@Cow[/mention] hey", SteamSession.MentionGroupSender(42, "Cow", "hey"));
        Assert.Equal("[mention=43]@member[/mention] hey", SteamSession.MentionGroupSender(43, null, "hey"));
        Assert.Equal("[mention=44]@A/mention[/mention] hey", SteamSession.MentionGroupSender(44, "A[/mention]", "hey"));
    }

    [Fact]
    public void AiReservationDoesNotSelectOrAdvancePreset()
    {
        var gate = new GroupReplyGate();
        var now = DateTimeOffset.UtcNow;
        var settings = new GroupReplySettings { Enabled = true, Messages = ["one", "two", "three"] };
        Assert.Equal(string.Empty, gate.Reserve(settings, 1, 2, 3, 4, (uint)now.ToUnixTimeSeconds(), 1, now, true));
        now = now.AddSeconds(10);
        Assert.Equal("one", gate.Reserve(settings, 1, 2, 3, 4, (uint)now.ToUnixTimeSeconds(), 2, now));
    }

    [Fact]
    public void CyclesIndependentlyPerRoomAndAccount()
    {
        var gate = new GroupReplyGate();
        Assert.Equal("one", Send(gate, 0));
        Assert.Equal("two", Send(gate, 1));
        Assert.Equal("one", Send(gate, 2, 9));
        Assert.Equal("three", Send(gate, 3));
        Assert.Equal("one", Send(gate, 4));
        Assert.Equal("one", Send(new GroupReplyGate(), 5));
    }
    [Fact]
    public void RejectsDisabledSelfDuplicateOldMessagesAndCooldownWithoutAdvancing()
    {
        var gate = new GroupReplyGate();
        settings.Enabled = false; Assert.Null(Send(gate, 0));
        settings.Enabled = true; Assert.Null(Send(gate, 0, sender: 4));
        Assert.Null(gate.Reserve(settings, 1, 2, 3, 4, (uint)now.AddMinutes(-2).ToUnixTimeSeconds(), 1, now));
        Assert.Equal("one", Send(gate, 0));
        Assert.Null(Send(gate, 0));
        settings.CooldownSeconds = 5;
        Assert.Null(Send(gate, 1));
        Assert.Equal("two", Send(gate, 5));
    }
    [Fact]
    public void RequiresExactlyThreeNonblankMessagesWhenEnabled()
    {
        settings.Messages[1] = " "; Assert.Throws<ArgumentException>(settings.Validate);
        settings.Enabled = false; settings.Validate();
        settings.Messages = []; Assert.Throws<ArgumentException>(settings.Validate);
        Assert.False(new AccountSettings().GroupReplies.Enabled);
    }
}
