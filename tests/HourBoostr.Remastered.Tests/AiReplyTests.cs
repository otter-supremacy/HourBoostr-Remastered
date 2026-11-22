using System.Net;
using System.Text;
using System.Text.Json;
using HourBoostr.Remastered;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class AiReplyTests : IDisposable
{
    private static readonly string TestKey = "gsk_" + new string('x', 25);
    private readonly string root = Path.Combine(Path.GetTempPath(), "hbr-ai-" + Guid.NewGuid().ToString("N"));
    private sealed class Handler(HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls; public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(token);
            Assert.Equal("https://api.groq.com/openai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return new(code) { Content = new StringContent(code == HttpStatusCode.OK ? "{\"choices\":[{\"message\":{\"content\":\"Hello [friend] @all\"}}]}" : "SECRET_PROVIDER_ERROR") };
        }
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
        ai.SaveKey(TestKey);
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
        ai.SaveKey(TestKey);
        Assert.Null(await ai.GenerateAsync("a", new(), "chat", "hello"));
        Assert.Null(await ai.GenerateAsync("b", new(), "chat", "hello"));
        Assert.Equal(1, handler.Calls); Assert.DoesNotContain("SECRET_PROVIDER_ERROR", JsonSerializer.Serialize(ai.Status()));
    }
    [Fact]
    public async Task PersistentDailyBudgetSurvivesServiceRecreation()
    {
        var store = new DataStore(root); var first = new AiReplyService(store, new(new Handler())); first.SaveKey(TestKey);
        Assert.NotNull(await first.GenerateAsync("a", new() { DailyLimit = 1 }, "first", "hello"));
        var handler = new Handler(); var second = new AiReplyService(new DataStore(root), new(handler));
        Assert.Null(await second.GenerateAsync("a", new() { DailyLimit = 1 }, "second", "hello")); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task SendsRecentContextAndSuppressesRepeatedProviderOutput()
    {
        var store = new DataStore(root); var handler = new Handler(); var ai = new AiReplyService(store, new(handler));
        ai.SaveKey(TestKey);
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
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
