using System.Net;
using System.Text;
using System.Text.Json;
using HourBoostr.Remastered;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class AiReplyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hbr-ai-" + Guid.NewGuid().ToString("N"));
    private sealed class Handler(HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls; public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(token);
            Assert.Equal("https://api.groq.com/openai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("HourBoostr-Remastered", Assert.Single(request.Headers.UserAgent).Product!.Name);
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            return new(code) { Content = new StringContent(code == HttpStatusCode.OK ? "{\"choices\":[{\"message\":{\"content\":\"Hello [friend] @all\"}}]}" : "SECRET_PROVIDER_ERROR") };
        }
    }
    [Fact]
    public void ModelCapsAreIndependentAndCarryForwardSharedUsage()
    {
        var store = new DataStore(root);
        using var http = new HttpClient();
        var now = DateTimeOffset.UtcNow;
        store.Write("ai-usage.json", new AiReplyUsage { Date = now.ToString("yyyy-MM-dd"), Accounts = new() { ["a"] = 499 } });
        var ai = new AiReplyService(store, http);
        Assert.True(ai.ReserveRequest("a", 500, now, AiReplyService.Model));
        Assert.False(ai.ReserveRequest("b", 500, now, AiReplyService.Model));
        Assert.True(ai.ReserveRequest("a", 500, now, AiReplyService.QwenModel));
        ai = new AiReplyService(new DataStore(root), http);
        Assert.False(ai.ReserveRequest("b", 500, now, AiReplyService.QwenModel));
        Assert.True(ai.ReserveRequest("a", 500, now.AddDays(1), AiReplyService.QwenModel));
    }

    [Fact]
    public async Task RandomAllowanceSurvivesRestartAndRequestRateLimitIsDistinct()
    {
        var now = DateTimeOffset.UtcNow;
        AiReplyService Open() => new(new DataStore(root), new HttpClient());
        Assert.False(Open().ReserveOccasionalPreset("cow", now, 1));
        Assert.True(Open().ReserveOccasionalPreset("cow", now, 0));
        Assert.True(Open().ReserveOccasionalPreset("cow", now, 0));
        Assert.False(Open().ReserveOccasionalPreset("cow", now, 0));
        Assert.True(Open().ReserveOccasionalPreset("cow", now.AddDays(1), 0));
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("requests per minute") };
        Assert.False(await AiReplyService.TokenLimitAsync(response, default));
        response.Headers.Add("x-ratelimit-remaining-tokens", "0");
        Assert.True(await AiReplyService.TokenLimitAsync(response, default));
    }
    [Fact]
    public async Task MissingKeyUsesFallbackWithoutNetworkAndDefaultsAreOff()
    {
        var store = new DataStore(root); var handler = new Handler(); var ai = new AiReplyService(store, new(handler));
        Assert.Null(await ai.GenerateAsync("a", new(), "chat", "hello")); Assert.Equal(0, handler.Calls);
        var settings = new AccountSettings(); Assert.False(settings.AiReplies.Groups); Assert.False(settings.AiReplies.PrivateMessages);
    }
    [Fact]
    public async Task KeyIsSeparateAndOneBoundedRequestGeneratesOnlyPlainText()
    {
        var store = new DataStore(root); var handler = new Handler(); var ai = new AiReplyService(store, new(handler));
        ai.SaveKey("gsk_abcdefghijklmnopqrstuv");
        var text = await ai.GenerateAsync("a", new(), "chat", new string('x', 2000));
        Assert.Equal("Hello (friend) all", text); Assert.Equal(1, handler.Calls);
        using var json = JsonDocument.Parse(handler.Body);
        Assert.Equal(AiReplyService.Model, json.RootElement.GetProperty("model").GetString());
        Assert.Equal(1000, json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.Length);
        Assert.DoesNotContain("gsk_", JsonSerializer.Serialize(store.Settings));
        Assert.DoesNotContain("gsk_", JsonSerializer.Serialize(ai.Status()));
        ai.SaveKey(""); Assert.Null(await ai.GenerateAsync("a", new(), "chat2", "hello")); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task RateLimitsBackOffWithoutRetryingOrLeakingErrors()
    {
        var store = new DataStore(root); var handler = new Handler(HttpStatusCode.TooManyRequests); var ai = new AiReplyService(store, new(handler));
        ai.SaveKey("gsk_abcdefghijklmnopqrstuv");
        Assert.Null(await ai.GenerateAsync("a", new(), "chat", "hello"));
        Assert.Null(await ai.GenerateAsync("b", new(), "chat", "hello"));
        Assert.Equal(1, handler.Calls); Assert.DoesNotContain("SECRET_PROVIDER_ERROR", JsonSerializer.Serialize(ai.Status()));
    }
    [Fact]
    public async Task PersistentDailyBudgetSurvivesServiceRecreation()
    {
        var store = new DataStore(root); var first = new AiReplyService(store, new(new Handler())); first.SaveKey("gsk_abcdefghijklmnopqrstuv");
        Assert.NotNull(await first.GenerateAsync("a", new() { DailyLimit = 1 }, "first", "hello"));
        var handler = new Handler(); var second = new AiReplyService(new DataStore(root), new(handler));
        Assert.Null(await second.GenerateAsync("a", new() { DailyLimit = 1 }, "second", "hello")); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task SendsRecentContextAndSuppressesRepeatedProviderOutput()
    {
        var store = new DataStore(root); var handler = new Handler(); var ai = new AiReplyService(store, new(handler));
        ai.SaveKey("gsk_abcdefghijklmnopqrstuv");
        var settings = new AiReplySettings { CooldownSeconds = 2 };
        Assert.NotNull(await ai.GenerateAsync("a", settings, "chat", "first message"));
        await Task.Delay(2200);
        Assert.Null(await ai.GenerateAsync("a", settings, "chat", "second message"));
        using var json = JsonDocument.Parse(handler.Body);
        var turns = json.RootElement.GetProperty("messages");
        Assert.Equal(4, turns.GetArrayLength());
        Assert.Equal("first message", turns[1].GetProperty("content").GetString());
        Assert.Equal("assistant", turns[2].GetProperty("role").GetString());
        Assert.Equal("second message", turns[3].GetProperty("content").GetString());
        Assert.Equal(2, handler.Calls);
    }
    [Fact]
    public void RejectsInvalidSettingsAndKeys()
    {
        Assert.Throws<ArgumentException>(() => new AiReplySettings { DailyLimit = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new AiReplySettings { CooldownSeconds = 0 }.Validate());
        var ai = new AiReplyService(new DataStore(root), new(new Handler()));
        Assert.Throws<ArgumentException>(() => ai.SaveKey("gsk_a\r\nbad"));
    }
    [Fact]
    public void ClearHostilityIsSeparateFromNormalGameBanter()
    {
        Assert.False(AiReplyService.IsClearlyHostile("gg, what a good game"));
        Assert.True(AiReplyService.IsClearlyHostile("Shut up, you idiot"));
        Assert.True(AiReplyService.IsClearlyHostile("f*ck you"));
        Assert.True(AiReplyService.IsClearlyHostile("which model are you? are you a fking ai?"));
        Assert.False(AiReplyService.IsClearlyHostile("that was a fucking good match"));
        Assert.False(AiReplyService.IsClearlyHostile("Kill the boss on the left"));
    }

    [Fact]
    public async Task CensoredProfanityStillRoutesToQwenWhenGptIsInBackoff()
    {
        var clock = new TestClock();
        var handler = new RoutingHandler { FailGpt = true };
        var ai = new AiReplyService(new DataStore(root), new(handler), clock);
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        var settings = new AiReplySettings { CooldownSeconds = 2 };
        Assert.Null(await ai.GenerateAsync("a", settings, "friend:1", "Nice match"));
        clock.Advance();
        Assert.NotNull(await ai.GenerateAsync("a", settings, "friend:1", "which model are you? are you a fking ai?\nsay bitch"));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("_qwen_", handler.Requests[1].Key);
        Assert.Contains("Matching ordinary profanity", handler.Requests[1].Body);
    }

    [Fact]
    public async Task HostileChatUsesTheSecondKeyAndItsOwnBudget()
    {
        var clock = new TestClock();
        var responder = new RoutingHandler();
        var store = new DataStore(root);
        var ai = new AiReplyService(store, new(responder), clock);
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        var reply = await ai.GenerateAsync("account", new() { DailyLimit = 1 }, "room", "shut up");
        Assert.NotNull(reply);
        var request = Assert.Single(responder.Requests);
        Assert.Equal("gsk_qwen_fixture_not_a_real_key", request.Key);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(AiReplyService.QwenModel, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("none", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("include_reasoning", out _));
        Assert.DoesNotContain("gsk_", JsonSerializer.Serialize(ai.Status()));
        clock.Advance();
        Assert.NotNull(await ai.GenerateAsync("account", new() { DailyLimit = 1 }, "other", "hi"));
        Assert.Equal(2, responder.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrMalformedQwenReplyFallsBackToGptOnce(bool malformed)
    {
        var clock = new TestClock();
        var responder = new RoutingHandler { FailQwen = !malformed, MalformedQwen = malformed };
        var ai = new AiReplyService(new DataStore(root), new(responder), clock);
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        var settings = new AiReplySettings { CooldownSeconds = 2 };
        Assert.Equal("catch you later", await ai.GenerateAsync("account", settings, "room", "shut up"));
        Assert.Equal(2, responder.Requests.Count);
        Assert.Equal("gsk_qwen_fixture_not_a_real_key", responder.Requests[0].Key);
        Assert.Equal("gsk_gpt_fixture_not_a_real_key", responder.Requests[1].Key);
        clock.Advance();
        Assert.NotNull(await ai.GenerateAsync("account", settings, "other room", "hello"));
        Assert.Equal(3, responder.Requests.Count);
        Assert.Equal("gsk_gpt_fixture_not_a_real_key", responder.Requests[2].Key);
    }

    [Fact]
    public async Task RejectedToneSchemaFallsBackToPlainGptAndKeepsOtherChatsAvailable()
    {
        var clock = new TestClock();
        var handler = new RoutingHandler { RejectSchema = true };
        var ai = new AiReplyService(new DataStore(root), new(handler), clock);
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        Assert.Equal("catch you later", await ai.GenerateAsync("a", new(), "friend:1", "Sorry about that. Good night."));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("response_format", handler.Requests[0].Body);
        Assert.DoesNotContain("response_format", handler.Requests[1].Body);
        clock.Advance();
        Assert.NotNull(await ai.GenerateAsync("a", new(), "friend:2", "Good game"));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task FailedHandoffUsesTheReplyFromTheFirstGptRequest()
    {
        var handler = new RoutingHandler { RequestHandoff = true, FailQwen = true, GptHandoffReply = "let's leave it there" };
        var ai = new AiReplyService(new DataStore(root), new(handler), new TestClock());
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        Assert.Equal("let's leave it there", await ai.GenerateAsync("a", new(), "friend:1", "Was that really the best you could do?"));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("_gpt_", handler.Requests[0].Key);
        Assert.Contains("_qwen_", handler.Requests[1].Key);
    }

    [Fact]
    public async Task RemovingQwenLeavesPrimaryKeyAndExistingBehavior()
    {
        var responder = new RoutingHandler();
        var ai = new AiReplyService(new DataStore(root), new(responder), new TestClock());
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        ai.SaveQwenKey("");
        Assert.NotNull(await ai.GenerateAsync("account", new(), "room", "shut up"));
        Assert.Equal("gsk_gpt_fixture_not_a_real_key", Assert.Single(responder.Requests).Key);
    }

    [Fact]
    public async Task GptToneHandoffUsesQwenPromptAndCountsBothRequests()
    {
        var clock = new TestClock();
        var handler = new RoutingHandler { RequestHandoff = true };
        var store = new DataStore(root);
        var ai = new AiReplyService(store, new(handler), clock);
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        var reply = await ai.GenerateAsync("a", new() { Instructions = "GPT tone", QwenInstructions = "Qwen tone" },
            "chat", "You weren't worth reading", startWithModel: AiReplyService.Model);
        Assert.Equal("catch you later", reply);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("GPT tone", handler.Requests[0].Body);
        Assert.DoesNotContain("Qwen tone", handler.Requests[0].Body);
        Assert.Contains("Qwen tone", handler.Requests[1].Body);
        Assert.DoesNotContain("GPT tone", handler.Requests[1].Body);
        using var status = JsonDocument.Parse(JsonSerializer.Serialize(ai.Status(), JsonDefaults.Options));
        Assert.True(status.RootElement.GetProperty("lastReplyHandedOff").GetBoolean());
        Assert.Equal(AiReplyService.QwenModel, status.RootElement.GetProperty("lastReplyModel").GetString());
        Assert.Equal(2, status.RootElement.GetProperty("requestsToday").GetInt32());
        var journal = new GroqTokenJournal(new DataStore(root));
        var usage = journal.Snapshot(clock.GetUtcNow());
        Assert.Equal(new GroqModelTokens(20, 6, 26, 1), usage[0]);
        Assert.Equal(new GroqModelTokens(30, 8, 38, 1), usage[1]);
        Assert.Equal(new GroqModelTokens(), journal.Snapshot(clock.GetUtcNow().AddDays(1))[0]);
    }

    [Fact]
    public async Task HandoffChargesEachModelSeparately()
    {
        var responder = new RoutingHandler { RequestHandoff = true };
        var ai = new AiReplyService(new DataStore(root), new(responder), new TestClock());
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        Assert.NotNull(await ai.GenerateAsync("a", new() { DailyLimit = 1 }, "chat", "you were a waste of time",
            startWithModel: AiReplyService.Model));
        Assert.Equal(2, responder.Requests.Count);
    }

    [Fact]
    public async Task ToneStaysInOneFriendOrGroupSenderAndCalmReplyClearsIt()
    {
        var clock = new TestClock();
        var handler = new RoutingHandler();
        var ai = new AiReplyService(new DataStore(root), new(handler), clock);
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        var settings = new AiReplySettings { CooldownSeconds = 2, Instructions = "GPT normal", QwenInstructions = "Qwen hostile" };
        async Task<(string Key, string Body)> Send(string chat, string message, ulong? sender = null)
        {
            await ai.GenerateAsync("account", settings, chat, message, groupSender: sender);
            clock.Advance();
            return handler.Requests.Last();
        }
        var rudeFriend = await Send("friend:100", "Shut up, you idiot");
        Assert.Contains("_qwen_", rudeFriend.Key);
        var otherFriend = await Send("friend:101", "Are you there?");
        Assert.Contains("_gpt_", otherFriend.Key);
        Assert.DoesNotContain("recently hostile", otherFriend.Body);
        var calmerFriend = await Send("friend:100", "Sorry, that was uncalled for.");
        Assert.Contains("_gpt_", calmerFriend.Key);
        Assert.Contains("recently hostile", calmerFriend.Body);
        var nextMessage = await Send("friend:100", "Let's play tomorrow.");
        Assert.DoesNotContain("recently hostile", nextMessage.Body);

        Assert.Contains("_qwen_", (await Send("group:20:3", "shut up", 100)).Key);
        var otherGroup = await Send("group:21:3", "nice match", 100);
        Assert.Contains("_gpt_", otherGroup.Key);
        Assert.DoesNotContain("recently hostile", otherGroup.Body);
        var sameGroupBystander = await Send("group:20:3", "good evening", 101);
        Assert.Contains("_gpt_", sameGroupBystander.Key);
        Assert.DoesNotContain("recently hostile", sameGroupBystander.Body);
        Assert.DoesNotContain("shut up", sameGroupBystander.Body);
    }

    [Fact]
    public async Task HostileModeExpiresAtFiveMinutesWhileConversationMemoryStays()
    {
        var clock = new TestClock();
        var responder = new RoutingHandler();
        var ai = new AiReplyService(new DataStore(root), new(responder), clock);
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        var settings = new AiReplySettings { CooldownSeconds = 2 };
        Assert.NotNull(await ai.GenerateAsync("a", settings, "friend:1", "shut up"));
        clock.Advance(TimeSpan.FromMinutes(5));
        await ai.GenerateAsync("a", settings, "friend:1", "Hello again");
        var next = responder.Requests.Last();
        Assert.Contains("_gpt_", next.Key);
        Assert.DoesNotContain("recently hostile", next.Body);
        Assert.Contains("shut up", next.Body);
    }

    [Fact]
    public async Task NormalChatKeepsItsPresetIfGptIsUnavailable()
    {
        var clock = new TestClock();
        var responder = new RoutingHandler { FailGpt = true };
        var ai = new AiReplyService(new DataStore(root), new(responder), clock);
        ai.SaveKey("gsk_gpt_fixture_not_a_real_key");
        ai.SaveQwenKey("gsk_qwen_fixture_not_a_real_key");
        Assert.Null(await ai.GenerateAsync("a", new(), "friend:1", "hello"));
        clock.Advance();
        Assert.Null(await ai.GenerateAsync("a", new(), "friend:2", "hi"));
        Assert.Contains("_gpt_", Assert.Single(responder.Requests).Key);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-11-27T01:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan? elapsed = null) => now += elapsed ?? TimeSpan.FromSeconds(3);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public bool FailQwen { get; init; }
        public bool MalformedQwen { get; init; }
        public bool FailGpt { get; init; }
        public bool RejectSchema { get; init; }
        public bool RequestHandoff { get; init; }
        public string GptHandoffReply { get; init; } = "";
        public List<(string Key, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var key = request.Headers.Authorization!.Parameter!;
            Requests.Add((key, await request.Content!.ReadAsStringAsync(token)));
            var qwen = key.Contains("_qwen_");
            if ((FailQwen && qwen) || (FailGpt && !qwen)) return new(HttpStatusCode.TooManyRequests);
            if (MalformedQwen && qwen) return new(HttpStatusCode.OK) { Content = new StringContent("{") };
            var structured = !qwen && Requests.Last().Body.Contains("response_format", StringComparison.Ordinal);
            if (structured && RejectSchema) return new(HttpStatusCode.BadRequest);
            var content = structured
                ? JsonSerializer.Serialize(new { handoff = RequestHandoff, reply = RequestHandoff ? GptHandoffReply : "thanks for the message" })
                : "catch you later";
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content } } },
                usage = new { prompt_tokens = qwen ? 30 : 20, completion_tokens = qwen ? 8 : 6,
                    total_tokens = qwen ? 38 : 26 }
            })) };
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
