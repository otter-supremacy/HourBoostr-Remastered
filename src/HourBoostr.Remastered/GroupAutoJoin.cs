using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SteamKit2;

namespace HourBoostr.Remastered;

public sealed record AutoJoinSettings
{
    public bool Enabled { get; init; }
    public bool DirectInvites { get; init; } = true;
    public bool GroupLinks { get; init; } = true;
    public bool PrivateLinks { get; init; } = true;
    public bool SmartDetection { get; init; }
    public int DailyLimit { get; init; } = 5;
}

public sealed record GroupJoinActivity(DateTimeOffset At, string Source, string Result);
public sealed record GroupJoinState(string Owner)
{
    public DateOnly Day { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset RetryAfter { get; set; }
    public List<GroupJoinActivity> Activity { get; init; } = [];
    public Dictionary<string, DateTimeOffset> Recent { get; init; } = [];
}

internal enum JoinSource { Direct, GroupChat, PrivateChat }
internal enum GroupKind { ClanInvite, CommunityId, CommunityName, ChatLink, ChatInvite }
internal sealed record GroupInvitation(GroupKind Kind, string Value, ulong ChatId = 0)
{
    // Extract whole candidates so an official URL embedded inside a third-party URL is not used.
    private static readonly Regex Candidates = new(@"(?<![\p{L}\p{N}_:/=?.&%-])https://[^\s<>\[\]""']+", RegexOptions.IgnoreCase);
    private static readonly Regex GroupUrl = new(
        @"\Ahttps://(?<host>(?:www\.)?steamcommunity\.com|s\.team)(?::443)?/(?<type>groups|gid|chat|chat/invite)/(?<id>[a-z0-9_-]{1,80})/?(?<language>\?l=[a-z]{2,24})?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Markup = new(
        @"!?\[[^\[\]\r\n]*\]\(\s*(?:<(?<markdown>[^\s<>]+)>|(?<markdown>[^\s()]+))(?:\s+[^)\r\n]*)?\)" +
        @"|\[url(?:\s*=\s*|\s+)(?:""(?<bbcode>[^""\r\n]+)""|'(?<bbcode>[^'\r\n]+)'|(?<bbcode>[^\s\[\]<>""']+))\].*?(?:\[/url\s*\]|\z)" +
        @"|\[url\](?<bare>[^\[\]\s]+)\[/url\s*\]" +
        @"|\[(?<tag>inviteurl|invite)(?<attributes>(?:\s+[a-z][a-z0-9]*\s*=\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s\[\]<>""']+))+)\s*\]" +
        @"|\[url\b[^\]\r\n]*\].*?(?:\[/url\s*\]|\z)" +
        @"|<a\b[^>\r\n]*>.*?(?:</a\s*>|\z)" +
        @"|\[(?:tradeofferlink|tradeoffer|gameinvite|lobbyinvite|img)\b[^\]\r\n]*\].*?(?:\[/(?:tradeofferlink|tradeoffer|gameinvite|lobbyinvite|img)\s*\]|\z)" +
        @"|\[og\b[^\]\r\n]*\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking);
    private static readonly Regex InviteAttribute = new(
        @"(?<name>[a-z][a-z0-9]*)\s*=\s*(?:""(?<value>[^""\r\n]*)""|'(?<value>[^'\r\n]*)'|(?<value>[^\s\[\]<>""']+))",
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    private static readonly Regex InviteCode = new(@"\A[a-zA-Z0-9_-]{1,80}\z", RegexOptions.NonBacktracking);
    private static readonly Regex LinkLike = new(@"https?://|steam://|\[(?:url|invite(?:url)?|tradeoffer(?:link)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool ContainsLinkMarkup(string text) => LinkLike.IsMatch(text);

    public static GroupInvitation[] Parse(string text, bool allowDirectChatInvite = false)
    {
        var links = new List<GroupInvitation>();
        // The clickable URL is authoritative. A label can show a different URL.
        var plain = Markup.Replace(text, match =>
        {
            var target = match.Groups["bbcode"].Success ? match.Groups["bbcode"] : match.Groups["bare"];
            if (match.Groups["markdown"].Success && match.Value[0] != '!') target = match.Groups["markdown"];
            var invitation = match.Groups["tag"].Success
                ? ParseInviteCard(match, allowDirectChatInvite)
                : target.Success ? ParseUrl(target.Value) : null;
            if (invitation is not null && links.Count < 8) links.Add(invitation);
            return " ";
        });
        return Candidates.Matches(plain)
            .Select(match => ParseUrl(match.Value.TrimEnd('.', ',', '!', ')', ';', '}')))
            .OfType<GroupInvitation>().Concat(links).Distinct().Take(8).ToArray();
    }

    private static GroupInvitation? ParseInviteCard(Match match, bool allowDirectChatInvite)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attr in InviteAttribute.Matches(match.Groups["attributes"].Value))
            if (!attributes.TryAdd(attr.Groups["name"].Value, attr.Groups["value"].Value)) return null;

        if (match.Groups["tag"].Value.Equals("inviteurl", StringComparison.OrdinalIgnoreCase))
            return attributes.TryGetValue("invitecode", out var code) && InviteCode.IsMatch(code)
                ? new(GroupKind.ChatLink, code) : null;

        if (!allowDirectChatInvite || !attributes.TryGetValue("chatgroupid", out var id)
            || !ulong.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var groupId)
            || groupId == 0)
            return null;
        if (attributes.TryGetValue("chatid", out var room) && !ulong.TryParse(room,
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)) return null;
        return new(GroupKind.ChatInvite, groupId.ToString(), room is null ? 0 : ulong.Parse(room));
    }

    private static GroupInvitation? ParseUrl(string value)
    {
        if (value.Length > 160) return null;
        var match = GroupUrl.Match(value);
        if (!match.Success) return null;
        var id = match.Groups["id"].Value;
        if (!InviteCode.IsMatch(id)) return null;
        var kind = match.Groups["type"].Value.ToLowerInvariant();
        if (match.Groups["host"].Value.Equals("s.team", StringComparison.OrdinalIgnoreCase))
            return kind == "chat" && !match.Groups["language"].Success ? new(GroupKind.ChatLink, id) : null;

        if (kind == "chat/invite") return new(GroupKind.ChatLink, id);
        if (kind == "groups") return new(GroupKind.CommunityName, id.ToLowerInvariant());
        if (kind == "gid" && ulong.TryParse(id, out var clan) && IsClan(clan))
            return new(GroupKind.CommunityId, clan.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return null;
    }

    // Enforce the same value constraints at the Steam transport boundary.
    public bool IsValidTarget() => Kind switch
    {
        GroupKind.ChatLink or GroupKind.CommunityName => InviteCode.IsMatch(Value) && ChatId == 0,
        GroupKind.CommunityId or GroupKind.ClanInvite => ulong.TryParse(Value,
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var clan)
            && IsClan(clan) && ChatId == 0,
        GroupKind.ChatInvite => ulong.TryParse(Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var group) && group > 0,
        _ => false
    };

    public static bool IsClan(ulong id)
    {
        var steamId = new SteamID(id);
        return steamId.IsValid && steamId.AccountType == EAccountType.Clan
            && steamId.AccountUniverse == EUniverse.Public && steamId.AccountInstance == 0;
    }

    public string Key() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Kind}:{Value}")));
}

internal sealed record ResolvedGroup(string Key, bool AlreadyJoined, Func<CancellationToken, Task<string>> Join);
internal sealed class GroupJoinException(string status, bool rateLimited = false) : Exception(status)
{
    public bool RateLimited { get; } = rateLimited;
}

internal sealed class GroupAutoJoin
{
    private sealed record Work(string Owner, JoinSource Source, GroupInvitation Invite);
    private sealed record RecentLink(string Owner, JoinSource Source, DateTimeOffset At, GroupInvitation[] Links);
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(24) { SingleReader = true });
    private readonly HashSet<Work> waiting = [];
    private readonly Dictionary<string, RecentLink> context = new();
    private readonly object gate = new();
    private readonly DataStore store;
    private readonly string file;
    private readonly Func<string, AutoJoinSettings?> settings;
    private readonly Func<string, GroupInvitation, CancellationToken, Task<ResolvedGroup>> resolve;
    private readonly Func<string, int, CancellationToken, Task<int?>>? interpret;
    private readonly TimeProvider clock;
    private readonly CancellationToken stop;
    private readonly Task loop;
    private GroupJoinState? state;
    private sealed record HistoryCache(string Owner, GroupJoinActivity[] Entries);
    private HistoryCache? history;
    private DateTimeOffset nextResolve;
    private int interpreting;
    private DateTimeOffset nextInterpret;
    private static readonly Regex Reference = new(@"\b(?:join|get in|hop in)\b.{0,80}\b(?:above|earlier|sent|posted|that|link)\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private static readonly Regex InviteLanguage = new(@"\b(?:join|group|clan|invite|come in|get in|hop in)\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    public GroupAutoJoin(DataStore store, string id, Func<string, AutoJoinSettings?> settings,
        Func<string, GroupInvitation, CancellationToken, Task<ResolvedGroup>> resolve,
        Func<string, int, CancellationToken, Task<int?>>? interpret, CancellationToken stop, TimeProvider? clock = null)
    {
        this.store = store;
        file = $"group-auto-join-{id}.json";
        this.settings = settings;
        this.resolve = resolve;
        this.interpret = interpret;
        this.stop = stop;
        this.clock = clock ?? TimeProvider.System;
        loop = Task.Run(Run);
    }

    public GroupJoinActivity[] History(string? owner)
    {
        if (string.IsNullOrEmpty(owner)) return [];
        if (Volatile.Read(ref history) is { } cached && cached.Owner == owner) return cached.Entries;
        try
        {
            var saved = store.Read<GroupJoinState>(file);
            var entries = saved?.Owner == owner ? saved.Activity.ToArray() : [];
            Interlocked.CompareExchange(ref history, new(owner, entries), null);
            return entries;
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return []; }
    }

    internal static bool Allows(AutoJoinSettings? value, JoinSource source) => value is { Enabled: true } && source switch
    {
        JoinSource.Direct => value.DirectInvites,
        JoinSource.GroupChat => value.GroupLinks,
        JoinSource.PrivateChat => value.PrivateLinks,
        _ => false
    };

    public bool Submit(string owner, JoinSource source, GroupInvitation invite)
    {
        if (!invite.IsValidTarget()) return false;
        lock (gate)
        {
            if (stop.IsCancellationRequested) return false;
            var item = new Work(owner, source, invite);
            if (waiting.Contains(item) || !queue.Writer.TryWrite(item)) return false;
            waiting.Add(item);
            return true;
        }
    }

    public bool Observe(string owner, JoinSource source, string scope, string message, AutoJoinSettings? config)
    {
        if (!Allows(config, source) || string.IsNullOrWhiteSpace(message) || message.Length > 16_384) return false;
        var now = clock.GetUtcNow();
        var links = GroupInvitation.Parse(message, allowDirectChatInvite: source == JoinSource.PrivateChat);
        var key = $"{source}:{scope}";
        if (links.Length != 0)
        {
            lock (gate)
            {
                if (context.Count >= 32 && !context.ContainsKey(key)) context.Remove(context.MinBy(pair => pair.Value.At).Key);
                context[key] = new(owner, source, now, links);
            }
            foreach (var link in links) Submit(owner, source, link);
            return true;
        }

        if (GroupInvitation.ContainsLinkMarkup(message)) return false;

        RecentLink? earlier;
        lock (gate) context.TryGetValue(key, out earlier);
        if (earlier is null || earlier.Owner != owner || now - earlier.At > TimeSpan.FromMinutes(5)) return false;
        if (earlier.Links.Length == 1 && Reference.IsMatch(message))
        {
            Submit(owner, source, earlier.Links[0]);
            return true;
        }
        if (config?.SmartDetection != true || interpret is null || message.Length > 400 || now < nextInterpret
            || !InviteLanguage.IsMatch(message)
            || Interlocked.CompareExchange(ref interpreting, 1, 0) != 0) return false;
        nextInterpret = now.AddSeconds(30);
        _ = Task.Run(() => Interpret(message, earlier));
        return false;
    }

    private async Task Interpret(string text, RecentLink earlier)
    {
        try
        {
            var index = await interpret!(text, earlier.Links.Length, stop);
            if (index is >= 0 && index < earlier.Links.Length && settings(earlier.Owner)?.SmartDetection == true)
                Submit(earlier.Owner, earlier.Source, earlier.Links[index.Value]);
        }
        catch (Exception) { /* Optional inference never affects Steam processing. */ }
        finally { Interlocked.Exchange(ref interpreting, 0); }
    }

    public async Task StopAsync()
    {
        queue.Writer.TryComplete();
        await loop;
    }

    private async Task Run()
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stop))
            {
                try { await Process(item); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception) { /* An unavailable local store fails closed before any new join. */ }
                finally { lock (gate) waiting.Remove(item); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task Process(Work item)
    {
        var config = settings(item.Owner);
        if (!Allows(config, item.Source)) return;
        var current = state?.Owner == item.Owner ? state : store.Read<GroupJoinState>(file);
        if (current?.Owner != item.Owner) current = new(item.Owner);
        state = current;
        var wait = nextResolve - clock.GetUtcNow();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, stop);
        var now = clock.GetUtcNow();
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        if (current.Day != day) { current.Day = day; current.Attempts = 0; }
        if (now < current.RetryAfter) return;
        foreach (var key in current.Recent.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) current.Recent.Remove(key);
        var alias = item.Invite.Key();
        if (current.Recent.ContainsKey(alias)) return;
        if (current.Attempts >= config!.DailyLimit)
        {
            if (current.Activity.LastOrDefault()?.Result != "Daily limit reached")
                Record(current, item.Source, now, "Daily limit reached");
            return;
        }
        var canonical = alias;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            nextResolve = now.AddSeconds(2);
            var group = await resolve(item.Owner, item.Invite, deadline.Token);
            canonical = group.Key;
            if (current.Recent.ContainsKey(canonical)) return;
            if (!group.AlreadyJoined)
            {
                config = settings(item.Owner);
                if (!Allows(config, item.Source) || current.Attempts >= config!.DailyLimit) return;
                // Reserve the attempt before any Steam write, including across a process crash.
                current.Attempts++;
                Remember(current, alias, canonical, now.AddMinutes(10));
                try { store.Write(file, current); }
                catch { state = null; throw; }
            }
            var result = group.AlreadyJoined ? "Already a member" : await group.Join(deadline.Token);
            Remember(current, alias, canonical, result is "Already a member" or "Joined Steam Community group" or "Joined Steam group chat"
                ? now.AddHours(24) : now.AddMinutes(10));
            Record(current, item.Source, now, result);
        }
        catch (GroupJoinException error)
        {
            if (error.RateLimited) current.RetryAfter = now.AddMinutes(5);
            Remember(current, alias, canonical, now.AddMinutes(10));
            Record(current, item.Source, now, error.Message);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            if (state is null) throw;
            Remember(current, alias, canonical, now.AddMinutes(10));
            Record(current, item.Source, now, "Steam could not confirm the join");
        }
    }

    private static void Remember(GroupJoinState current, string alias, string key, DateTimeOffset until)
    {
        current.Recent[alias] = until;
        current.Recent[key] = until;
        while (current.Recent.Count > 128) current.Recent.Remove(current.Recent.MinBy(pair => pair.Value).Key);
    }

    private void Record(GroupJoinState current, JoinSource source, DateTimeOffset now, string result)
    {
        current.Activity.Add(new(now, source switch
        {
            JoinSource.Direct => "Steam invitation",
            JoinSource.PrivateChat => "Private message",
            _ => "Group chat"
        }, result));
        while (current.Activity.Count > 25) current.Activity.RemoveAt(0);
        store.Write(file, current);
        Volatile.Write(ref history, new(current.Owner, current.Activity.ToArray()));
    }
}
