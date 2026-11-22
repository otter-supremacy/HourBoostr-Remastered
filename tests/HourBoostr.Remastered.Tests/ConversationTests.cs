using HourBoostr.Remastered;
using Xunit;
namespace HourBoostr.Remastered.Tests;
public sealed class ConversationTests
{
    [Fact]
    public void MemoryIsBoundedAndIsolatedAndExpires()
    {
        var memory = new ConversationMemory(); var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 6; i++) memory.Remember("account:group", "input" + i, "reply" + i, now);
        var turns = memory.Recent("account:group", now);
        Assert.Equal(4, turns.Length); Assert.Equal("input4", turns[0].Content);
        Assert.Empty(memory.Recent("otheraccount:group", now)); Assert.Empty(memory.Recent("account:friend", now));
        Assert.True(ConversationMemory.Repeats(turns, "REPLY5!"));
        Assert.False(ConversationMemory.Repeats(turns, "a different response"));
        Assert.Empty(memory.Recent("account:group", now.AddMinutes(21)));
    }
}
