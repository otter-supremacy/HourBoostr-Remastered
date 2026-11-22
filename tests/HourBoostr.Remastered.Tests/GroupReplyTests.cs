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
