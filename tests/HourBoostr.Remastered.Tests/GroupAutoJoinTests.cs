using SteamKit2;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class GroupAutoJoinTests
{
    private const ulong Clan = 103582791429521409;
    private const string Owner = "owner";
    private const string Id = "account";

    [Fact]
    public void ParsesOnlySteamGroupTargets()
    {
        var links = GroupInvitation.Parse($"Hello, (https://steamcommunity.com/groups/Example/), https://www.steamcommunity.com/gid/{Clan}?l=en and [url=https://s.team/chat/Code-a]join[/url]. https://steamcommunity.com/groups/example");
        Assert.Equal([
            new(GroupKind.CommunityName, "example"),
            new(GroupKind.CommunityId, Clan.ToString()),
            new(GroupKind.ChatLink, "Code-a")
        ], links);
    }

    [Theory]
    [InlineData("http://steamcommunity.com/groups/example")]
    [InlineData("https://steamcommunity.com.evil.example/groups/example")]
    [InlineData("https://evil.example/?destination=https://steamcommunity.com/groups/example")]
    [InlineData("https://steamcommunity.com@evil.example/groups/example")]
    [InlineData("https://other@steamcommunity.com/groups/example")]
    [InlineData("https://steamcommunity.com:444/groups/example")]
    [InlineData("https://steamcommunity.com/groups/../groups/example")]
    [InlineData("https://steamcommunity.com/groups/example/members")]
    [InlineData("https://steamcommunity.com/groups/some%2fthing")]
    [InlineData("https://steamcommunity.com/gid/76561197960265729")]
    [InlineData("https://s.team/chat/")]
    [InlineData("https://s.team/chat/code?redirect=https://evil.example")]
    public void IgnoresUnrelatedOrMisleadingUrls(string input) =>
        Assert.Empty(GroupInvitation.Parse(input));

    [Theory]
    [InlineData("https://steamcommunity.com/chat/invite/AbCd1234")]
    [InlineData("[url=\"https://s.team/chat/AbCd1234\"]Join group[/url]")]
    [InlineData("[url='https://steamcommunity.com/chat/invite/AbCd1234']Join group[/url]")]
    [InlineData("[url https://steamcommunity.com/chat/invite/AbCd1234]Join group[/url]")]
    [InlineData("[url=https://steamcommunity.com/chat/invite/AbCd1234]Join group[/url]")]
    [InlineData("https://s.team/chat/AbCd1234 https://steamcommunity.com/chat/invite/AbCd1234")]
    public void SteamFormatsResolveToOneCaseSensitiveChatInvite(string message) =>
        Assert.Equal(new(GroupKind.ChatLink, "AbCd1234"), Assert.Single(GroupInvitation.Parse(message)));

    [Fact]
    public void ALinkLabelCannotSubstituteForAnUntrustedTarget() =>
        Assert.Empty(GroupInvitation.Parse("[url=https://unknown.example/]https://s.team/chat/AbCd1234[/url]"));

    [Theory]
    [InlineData("https://steamcommunity.com/tradeoffer/new/?partner=123&token=Example")]
    [InlineData("https://steamcommunity.com/linkfilter/?u=https%3A%2F%2Fs.team%2Fchat%2FExample")]
    [InlineData("https://s.team/p/Example")]
    [InlineData("steam://trading/123")]
    [InlineData("https://steamcommunity.com/chat/invite/Example?return=https://example.test")]
    [InlineData("https://steamcommunity.com/groups/Example#Trade")]
    [InlineData("https://steаmcommunity.com/groups/Example")] // Cyrillic a
    [InlineData("[https://s.team/chat/Example](https://example.test/ \"Group\")")]
    [InlineData("[https://s.team/chat/Example](<https://steamcommunity.com/tradeoffer/new/>)")]
    [InlineData("![https://s.team/chat/Example](https://example.test/image)")]
    [InlineData("[url=invalid destination]https://s.team/chat/Example[/url]")]
    [InlineData("<a href=\"https://example.test\">https://s.team/chat/Example</a>")]
    [InlineData("[tradeofferlink url=\"https://s.team/chat/Example\"]")]
    [InlineData("[invite chatgroupname=\"[url=https://s.team/chat/Example]title[/url]\"][/invite]")]
    [InlineData("[tradeofferlink]https://s.team/chat/Example[/tradeofferlink]")]
    [InlineData("[img]https://s.team/chat/Example[/img]")]
    public void RejectsTradeRedirectsAndMisleadingDestinations(string message) =>
        Assert.Empty(GroupInvitation.Parse(message, allowDirectChatInvite: true));

    [Fact]
    public void KeepsASeparateOfficialJoinLinkInAMixedMessage() =>
        Assert.Equal(new(GroupKind.ChatLink, "RealInvite"), Assert.Single(GroupInvitation.Parse(
            "[trade here](https://steamcommunity.com/tradeoffer/new/) or [join](https://s.team/chat/RealInvite)")));

    [Fact]
    public async Task TradeMessageDoesNotReuseEarlierGroupInvite()
    {
        using var fixture = new Fixture { Config = new() { Enabled = true, SmartDetection = true } };
        var aiCalls = 0;
        var resolutions = 0;
        var worker = fixture.Worker(default, (_, invite, _) =>
        {
            resolutions++;
            return Task.FromResult(new ResolvedGroup(invite.Key(), true, _ => throw new InvalidOperationException()));
        }, (_, _, _) =>
        {
            aiCalls++;
            return Task.FromResult<int?>(0);
        });
        Assert.True(worker.Observe(Owner, JoinSource.PrivateChat, "sender", "https://s.team/chat/Initial", fixture.Config));
        Assert.False(worker.Observe(Owner, JoinSource.PrivateChat, "sender",
            "join my group, see earlier https://steamcommunity.com/tradeoffer/new/", fixture.Config));
        Assert.False(worker.Submit(Owner, JoinSource.PrivateChat,
            new(GroupKind.ChatLink, "https://steamcommunity.com/tradeoffer/new/")));
        await worker.StopAsync();
        Assert.Equal(0, aiCalls);
        Assert.Equal(1, resolutions);
    }

    [Fact]
    public void PrivateSteamInviteCardUsesTheGroupAndOptionalChatId()
    {
        const string card = "[invite  chatgroupid=\"12345678\" chatgroupname=\"Friends Room Today\" chatid=\"502\"][/invite]";
        Assert.Empty(GroupInvitation.Parse(card));
        Assert.Equal(new(GroupKind.ChatInvite, "12345678", 502),
            Assert.Single(GroupInvitation.Parse(card, allowDirectChatInvite: true)));
    }

    [Theory]
    [InlineData("[invite chatgroupname=\"chatgroupid=123\"][/invite]")]
    [InlineData("[invite chatgroupid=\"123\" chatgroupid=\"456\"][/invite]")]
    [InlineData("[invite chatgroupid=\"0\"][/invite]")]
    [InlineData("[invite chatgroupid=\"123\" chatid=\"not-a-room\"][/invite]")]
    public void RejectsAmbiguousOrInvalidNativeInviteCards(string message) =>
        Assert.Empty(GroupInvitation.Parse(message, allowDirectChatInvite: true));

    [Fact]
    public async Task PrivateNativeInviteUsesTheExistingJoinQueueWithoutGroq()
    {
        using var fixture = new Fixture();
        GroupInvitation? delivered = null;
        var worker = fixture.Worker(default, (_, item, _) =>
        {
            delivered = item;
            return Task.FromResult(new ResolvedGroup(item.Key(), false, _ => Task.FromResult("Joined Steam group chat")));
        });
        Assert.True(worker.Observe(Owner, JoinSource.PrivateChat, "sender",
            "[invite  chatgroupid=\"12345678\" chatgroupname=\"Friends Room Today\"][/invite]", fixture.Config));
        await worker.StopAsync();
        Assert.Equal(new(GroupKind.ChatInvite, "12345678"), delivered);
        Assert.Equal("Joined Steam group chat", Assert.Single(worker.History(Owner)).Result);
    }

    [Fact]
    public void TheSteamInviteUrlCardPreservesCodeCase() =>
        Assert.Equal(new(GroupKind.ChatLink, "AbCd1234"),
            Assert.Single(GroupInvitation.Parse("[inviteurl invitecode=\"AbCd1234\"][/inviteurl]")));

    [Fact]
    public void AcknowledgementIsTheSteamClanIdAndAcceptFlag()
    {
        using var stream = new MemoryStream();
        var body = new ClanInviteAcknowledgement { Clan = Clan };
        body.Serialize(stream);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        Assert.Equal(EMsg.ClientAcknowledgeClanInvite, body.GetEMsg());
        Assert.Equal(Clan, reader.ReadUInt64());
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal(9, stream.Length);
    }

    [Fact]
    public async Task ExplicitInvitesAndLinksWorkWithoutAiAndRespectMembership()
    {
        using var fixture = new Fixture();
        var joined = new List<GroupInvitation>();
        using var stop = new CancellationTokenSource();
        var worker = fixture.Worker(stop.Token, (_, invite, _) =>
        {
            var already = invite.Kind == GroupKind.CommunityName;
            return Task.FromResult(new ResolvedGroup(invite.Key(), already, _ =>
            {
                joined.Add(invite);
                return Task.FromResult(invite.Kind == GroupKind.ChatLink ? "Joined Steam group chat" : "Joined Steam Community group");
            }));
        });
        var invite = new GroupInvitation(GroupKind.ClanInvite, Clan.ToString());
        // Steam's clan notification supplies the group and relationship, irrespective of the sender.
        Assert.True(worker.Submit(Owner, JoinSource.Direct, invite));
        Assert.False(worker.Submit(Owner, JoinSource.Direct, invite));
        Assert.True(worker.Observe(Owner, JoinSource.GroupChat, "room",
            "https://steamcommunity.com/groups/Example https://s.team/chat/Example", fixture.Config));
        await WaitFor(() => worker.History(Owner).Length == 3);
        Assert.Equal(2, joined.Count);
        Assert.Equal("Already a member", worker.History(Owner)[1].Result);
        await worker.StopAsync();
    }

    [Fact]
    public async Task ReconnectCannotSpendDailyBudgetAgain()
    {
        using var fixture = new Fixture { Config = new() { Enabled = true, DailyLimit = 1 } };
        var calls = 0;
        Task<ResolvedGroup> Resolve(string owner, GroupInvitation invite, CancellationToken token) => Task.FromResult(
            new ResolvedGroup(invite.Key(), false, _ =>
            {
                calls++;
                return Task.FromResult("Joined Steam Community group");
            }));
        var a = new GroupInvitation(GroupKind.ClanInvite, Clan.ToString());
        var original = fixture.Worker(default, Resolve);
        original.Submit(Owner, JoinSource.Direct, a);
        await original.StopAsync();
        var reconnected = fixture.Worker(default, Resolve);
        reconnected.Submit(Owner, JoinSource.Direct, a);
        reconnected.Submit(Owner, JoinSource.Direct, new(GroupKind.ClanInvite, (Clan + 1).ToString()));
        await reconnected.StopAsync();
        Assert.Equal(1, calls);
        Assert.Equal("Daily limit reached", reconnected.History(Owner).Last().Result);
        Assert.Empty(reconnected.History("different-owner"));
    }

    [Fact]
    public async Task RestrictionAndRateLimitDoNotClaimSuccess()
    {
        using var fixture = new Fixture();
        var tried = 0;
        var worker = fixture.Worker(default, (_, invite, _) =>
        {
            tried++;
            throw new GroupJoinException(invite.Value == "private" ? "Steam group is restricted" : "Steam rate limit", invite.Value != "private");
        });
        worker.Observe(Owner, JoinSource.PrivateChat, "stranger", "https://steamcommunity.com/groups/private https://s.team/chat/RateLimit https://s.team/chat/Skipped", fixture.Config);
        await worker.StopAsync();
        Assert.Equal(2, tried);
        Assert.Equal(["Steam group is restricted", "Steam rate limit"], worker.History(Owner).Select(entry => entry.Result));
    }

    [Fact]
    public async Task ReferencesStayInTheSameConversationAndOptionalAiFailureDoesNotBlockLinks()
    {
        using var fixture = new Fixture { Config = new() { Enabled = true, SmartDetection = true } };
        var inferenceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inference = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = fixture.Worker(default, (_, invite, _) =>
            Task.FromResult(new ResolvedGroup(invite.Key(), true, _ => throw new InvalidOperationException())),
            (_, _, _) => { inferenceStarted.SetResult(); return inference.Task; });
        Assert.True(worker.Observe(Owner, JoinSource.GroupChat, "room", "https://s.team/chat/Initial", fixture.Config));
        Assert.False(worker.Observe(Owner, JoinSource.GroupChat, "other-room", "join the group from earlier", fixture.Config));
        Assert.True(worker.Observe(Owner, JoinSource.GroupChat, "room", "join the group from earlier", fixture.Config));
        Assert.False(worker.Observe(Owner, JoinSource.GroupChat, "room", "get in our group bro", fixture.Config));
        await inferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(worker.Observe(Owner, JoinSource.PrivateChat, "sender", "https://s.team/chat/Second", fixture.Config));
        await WaitFor(() => worker.History(Owner).Length == 2);
        inference.SetException(new TimeoutException());
        await worker.StopAsync();
        Assert.All(worker.History(Owner), entry => Assert.Equal("Already a member", entry.Result));
    }

    [Fact]
    public async Task DisabledSourceDoesNotQueueWork()
    {
        using var fixture = new Fixture { Config = new() { Enabled = true, PrivateLinks = false, GroupLinks = false } };
        var worker = fixture.Worker(default, (_, _, _) => throw new InvalidOperationException());
        Assert.False(worker.Observe(Owner, JoinSource.PrivateChat, "sender", "https://s.team/chat/ignored", fixture.Config));
        Assert.False(worker.Observe(Owner, JoinSource.GroupChat, "room", "https://s.team/chat/ignored", fixture.Config));
        await worker.StopAsync();
        Assert.Empty(worker.History(Owner));
    }

    [Fact]
    public void SavedSettingsKeepDefaultsForOlderProfilesAndPersistPerAccount()
    {
        using var fixture = new Fixture();
        var store = fixture.Open();
        var account = store.Add("One");
        var other = store.Add("Two");
        Assert.False(account.AutoJoinGroups.Enabled);
        account.AutoJoinGroups = new() { Enabled = true, DailyLimit = 2, GroupLinks = false };
        store.Save(account);
        var reopened = fixture.Open();
        Assert.Equal(account.AutoJoinGroups, reopened.Account(account.Id)?.AutoJoinGroups);
        Assert.False(reopened.Account(other.Id)!.AutoJoinGroups.Enabled);
        account.AutoJoinGroups = new() { DailyLimit = 26 };
        Assert.Throws<ArgumentException>(account.Validate);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(200)]
    public async Task GroqProviderFailureOpensOnlyTheGroupDetectionCircuit(int status)
    {
        using var fixture = new Fixture();
        using var handler = new GroupDetectionHandler(status);
        using var http = new HttpClient(handler);
        var ai = new AiReplyService(fixture.Open(), http);
        ai.SaveKey("gsk_" + new string('x', 32));
        Assert.Null(await ai.InterpretGroupInviteAsync(Id, "Can you join that group?", 1, default));
        Assert.Null(await ai.InterpretGroupInviteAsync(Id, "Can you join that group?", 1, default));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, fixture.Open().Read<AiReplyUsage>("ai-usage.json")?.Accounts.Count ?? 0);
    }

    private sealed class GroupDetectionHandler(int status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                Content = new StringContent("invalid json")
            });
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"hourboostr-join-test-{Guid.NewGuid():N}");
        public DataStore Open() => new(root);
        public AutoJoinSettings Config { get; init; } = new() { Enabled = true };
        public GroupAutoJoin Worker(CancellationToken token, Func<string, GroupInvitation, CancellationToken, Task<ResolvedGroup>> resolve,
            Func<string, int, CancellationToken, Task<int?>>? interpret = null) =>
            new(new(root), Id, owner => owner == Owner ? Config : null, resolve, interpret, token);

        public void Dispose() => Directory.Delete(root, true);
    }
}
