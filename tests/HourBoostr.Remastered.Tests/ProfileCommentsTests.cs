using System.Text.Json;
using HourBoostr.Remastered;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class ProfileCommentsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"hbr-comments-{Guid.NewGuid():N}");
    private const string Account = "fixture";
    private const string Owner = "76561198000000001";
    private const string Author = "76561198000000002";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData("https://example.test/x", true)]
    [InlineData("example[dot]test", true)]
    [InlineData("example。test", true)]
    [InlineData("exa\u200bmple.test", true)]
    [InlineData("[url=example.test]hello[/url]", true)]
    [InlineData("GG. Thanks for playing!", false)]
    public void FiltersLinksBeforeScreening(string value, bool expected) =>
        Assert.Equal(expected, ProfileComments.ContainsLink(value));

    [Fact]
    public async Task NewPlainTextIsScreenedOnceAndBlockedTextIsNotStored()
    {
        var store = new DataStore(root);
        var inbox = new ProfileComments(store, Account);
        inbox.Configure(Owner, true, Now.AddMinutes(-1));
        var samples = new[]
        {
            new ProfileCommentInput("1", Author, Now.AddMinutes(-2), "Old comment"),
            new ProfileCommentInput("2", Author, Now, "Click https://example.test"),
            new ProfileCommentInput("3", Author, Now, "GG!"),
            new ProfileCommentInput("4", Author, Now, "Blocked by fixture")
        };
        var screened = new List<string>();
        Task<string> Screen(string comment, CancellationToken _)
        {
            screened.Add(comment);
            return Task.FromResult(comment == "GG!" ? "review" : "block");
        }
        await inbox.ObserveAsync(Owner, samples, Now, Screen, default);
        inbox = new ProfileComments(store, Account);
        await inbox.ObserveAsync(Owner, samples, Now.AddSeconds(30), Screen, default);
        Assert.Equal(new[] { "GG!", "Blocked by fixture" }, screened);
        var value = JsonSerializer.SerializeToElement(inbox.Read(Owner, Now, "Checked"), JsonDefaults.Options);
        Assert.Equal(2, value.GetProperty("blocked").GetInt32());
        var item = Assert.Single(value.GetProperty("items").EnumerateArray());
        Assert.Equal("GG!", item.GetProperty("text").GetString());
        Assert.Equal("review", item.GetProperty("verdict").GetString());
        Assert.DoesNotContain("Blocked by fixture", File.ReadAllText(store.Resolve("profile-comments-fixture.json")));
        inbox.Dismiss(Owner, "3");
        await inbox.ObserveAsync(Owner, samples, Now.AddSeconds(60), Screen, default);
        Assert.Equal(2, screened.Count);
        inbox.Configure(Owner, false, Now.AddSeconds(65));
        var paused = JsonSerializer.SerializeToElement(inbox.Read(Owner, Now, "Checked"), JsonDefaults.Options);
        Assert.Empty(paused.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task NoGroqKeyLeavesCommentForReview()
    {
        using var client = new HttpClient();
        var ai = new AiReplyService(new DataStore(root), client);
        Assert.Equal("review", await ai.ScreenCommentAsync(Account, 100, "GG", default));
    }

    [Theory]
    [InlineData("{\"verdict\":\"safe\"}", "safe")]
    [InlineData("{\"verdict\":\"block\"}", "block")]
    [InlineData("{\"verdict\":\"publish\"}", "review")]
    [InlineData("not json", "review")]
    public async Task GroqVerdictIsRestrictedAndUsesSharedBudget(string modelText, string expected)
    {
        var store = new DataStore(root);
        using var client = new HttpClient(new ModerationFixture(modelText));
        var ai = new AiReplyService(store, client);
        ai.SaveKey("gsk_fixture_only_not_a_real_key");
        Assert.Equal(expected, await ai.ScreenCommentAsync(Account, 100, "GG", default));
        Assert.Equal(1, store.Read<AiModelRequestUsage>("ai-usage.json")!.Gpt[Account]);
    }

    private sealed class ModerationFixture(string modelText) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.True(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
            Assert.Equal("GG", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content = modelText } } }
                }))
            };
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
