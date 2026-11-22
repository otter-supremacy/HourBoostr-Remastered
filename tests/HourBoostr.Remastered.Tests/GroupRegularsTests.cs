using HourBoostr.Remastered;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class GroupRegularsTests : IDisposable
{
    private const string Owner = "76561198000000001";
    private static readonly DateTimeOffset Today = DateTimeOffset.Parse("2026-11-20T09:00:00Z");
    private readonly string root = Path.Combine(Path.GetTempPath(), $"hbr-regulars-{Guid.NewGuid():N}");
    private readonly string account = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> joined = ["1", "2"];

    private GroupRegulars Open() => new(new DataStore(root), account);
    private static void Observe(GroupRegulars regulars, ulong sender, int day, string text = "hello", string group = "1") =>
        regulars.Observe(Owner, sender, "Regular", group, text, Today.AddDays(day), Today.AddDays(day));

    [Fact]
    public void BurstsAndActivityAloneDoNotQualify()
    {
        var regulars = Open();
        regulars.SaveGroups(Owner, ["1"]);
        for (int i = 0; i < 50; i++) Observe(regulars, 8, 0, $"!addme {Owner}");
        Assert.Empty(regulars.Suggestions(Owner, joined, Today));
        Observe(regulars, 9, -2);
        Observe(regulars, 9, -1);
        Observe(regulars, 9, 0, "!addme 76561198000000055");
        Assert.Empty(regulars.Suggestions(Owner, joined, Today));
        Observe(regulars, 9, 0, $"!addme {Owner}", group: "2");
        Assert.Empty(regulars.Suggestions(Owner, joined, Today));
    }

    [Fact]
    public void ConsentAndActivitySurviveRestartAndInviteIsReservedOnce()
    {
        var regulars = Open();
        regulars.SaveGroups(Owner, ["1"]);
        foreach (var sender in new ulong[] { 8, 9 })
        {
            Observe(regulars, sender, -2, "private fixture text");
            Observe(regulars, sender, -1);
            Observe(regulars, sender, 0, $"!addme {Owner}");
        }
        regulars = Open();
        Assert.Equal(2, regulars.Suggestions(Owner, joined, Today).Length);
        Assert.Empty(regulars.Suggestions(Owner, new HashSet<string>(), Today));
        int sends = 0;
        regulars.Act(Owner, 8, joined, Today, true, () => sends++);
        regulars = Open();
        Assert.Throws<ArgumentException>(() => regulars.Act(Owner, 8, joined, Today, true, () => sends++));
        Assert.Throws<ArgumentException>(() => regulars.Act(Owner, 9, joined, Today, true, () => sends++));
        regulars.Act(Owner, 9, joined, Today, false, () => sends++);
        Assert.Equal(1, sends);
        Assert.Empty(Open().Suggestions(Owner, joined, Today));
        Assert.DoesNotContain("private fixture text", File.ReadAllText(Path.Combine(root, $"group-regulars-{account}.json")));
    }

    [Fact]
    public void MutingClearsTrackingAndExpiredActivityDoesNotQualify()
    {
        var regulars = Open();
        regulars.SaveGroups(Owner, ["1"]);
        Observe(regulars, 8, -2);
        Observe(regulars, 8, -1);
        Observe(regulars, 8, 0, $"!addme {Owner}");
        Assert.Single(regulars.Suggestions(Owner, joined, Today));
        Assert.Empty(regulars.Suggestions(Owner, joined, Today.AddDays(14)));
        regulars.SaveGroups(Owner, []);
        regulars.SaveGroups(Owner, ["1"]);
        Assert.Empty(Open().Suggestions(Owner, joined, Today));
        Assert.Empty(Open().SelectedGroups("76561198000000002"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
