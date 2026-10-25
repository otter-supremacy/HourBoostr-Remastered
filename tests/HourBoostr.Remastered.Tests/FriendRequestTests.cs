using HourBoostr.Remastered;
using SteamKit2;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class FriendRequestTests
{
    [Theory]
    [InlineData(EFriendRelationship.None)]
    [InlineData(EFriendRelationship.Friend)]
    [InlineData(EFriendRelationship.Blocked)]
    [InlineData(EFriendRelationship.Ignored)]
    [InlineData(EFriendRelationship.RequestInitiator)]
    public void DoesNotAddUsersWithoutIncomingRequest(EFriendRelationship relationship) => Assert.False(new FriendRequestGate().TryReserve(true, 2, 1, EAccountType.Individual, relationship, DateTimeOffset.UtcNow));
    [Fact]
    public void RequiresEnabledIndividualIncomingRequestAndNeverSelf()
    {
        var gate = new FriendRequestGate(); var now = DateTimeOffset.UtcNow;
        Assert.False(gate.TryReserve(false, 2, 1, EAccountType.Individual, EFriendRelationship.RequestRecipient, now));
        Assert.False(gate.TryReserve(true, 2, 1, EAccountType.Clan, EFriendRelationship.RequestRecipient, now));
        Assert.False(gate.TryReserve(true, 1, 1, EAccountType.Individual, EFriendRelationship.RequestRecipient, now));
        Assert.True(gate.TryReserve(true, 2, 1, EAccountType.Individual, EFriendRelationship.RequestRecipient, now));
    }
    [Fact]
    public void SpacesRequestsSuppressesDuplicatesAndAllowsBoundedRetry()
    {
        var gate = new FriendRequestGate(); var now = DateTimeOffset.UtcNow;
        bool Accept(ulong id, DateTimeOffset time) => gate.TryReserve(true, id, 1, EAccountType.Individual, EFriendRelationship.RequestRecipient, time);
        Assert.True(Accept(2, now)); Assert.False(Accept(3, now.AddSeconds(1)));
        Assert.True(Accept(3, now.AddSeconds(2))); Assert.False(Accept(2, now.AddMinutes(5)));
        Assert.True(Accept(2, now.AddMinutes(10)));
    }
}
