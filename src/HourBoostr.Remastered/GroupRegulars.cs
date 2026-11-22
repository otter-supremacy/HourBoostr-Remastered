using System.Globalization;

namespace HourBoostr.Remastered;

public sealed record RegularGroup(string Id, string Name);
public sealed record RegularSuggestion(string SteamId, string Name, string GroupId, int ActiveDays);
public sealed record RegularSettings(string[] GroupIds);
public sealed record RegularAction(string Action, string SteamId);

public sealed class GroupRegulars(DataStore store, string accountId)
{
    private const int WindowDays = 14;
    private const int MaximumPeople = 512;
    private readonly object gate = new();
    private readonly string file = $"group-regulars-{accountId}.json";
    private RegularState? state;

    public sealed class RegularState
    {
        public string Owner { get; set; } = "";
        public string[] GroupIds { get; set; } = [];
        public Dictionary<ulong, Person> People { get; set; } = [];
        public DateTimeOffset LastRequestAt { get; set; }
    }

    public sealed class Person
    {
        public string Name { get; set; } = "";
        public Dictionary<string, HashSet<DateOnly>> Days { get; set; } = [];
        public Dictionary<string, DateTimeOffset> AskedAt { get; set; } = [];
        public DateTimeOffset DismissedUntil { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    private RegularState ForOwner(string owner)
    {
        state ??= store.Read<RegularState>(file) ?? new();
        if (state.Owner == owner) return state;
        state = new() { Owner = owner };
        return state;
    }

    public string[] SelectedGroups(string owner, DateTimeOffset? now = null)
    {
        lock (gate)
        {
            var current = ForOwner(owner);
            if (now is { } at && Prune(current, at)) store.Write(file, current);
            return [.. current.GroupIds];
        }
    }

    public void SaveGroups(string owner, string[] groups)
    {
        lock (gate)
        {
            var current = ForOwner(owner);
            current.GroupIds = groups;
            var enabled = groups.ToHashSet(StringComparer.Ordinal);
            foreach (var person in current.People.Values)
            {
                foreach (var id in person.Days.Keys.Where(id => !enabled.Contains(id)).ToArray())
                    person.Days.Remove(id);
                foreach (var id in person.AskedAt.Keys.Where(id => !enabled.Contains(id)).ToArray())
                    person.AskedAt.Remove(id);
            }
            foreach (var id in current.People.Where(pair => pair.Value.Days.Count == 0).Select(pair => pair.Key).ToArray())
                current.People.Remove(id);
            store.Write(file, current);
        }
    }

    public bool Observe(string owner, ulong sender, string name, string group, string text, DateTimeOffset sentAt, DateTimeOffset now)
    {
        if (sentAt < now.AddMinutes(-2) || sentAt > now.AddSeconds(10)) return false;
        var asked = string.Equals(text.Trim(), $"!addme {owner}", StringComparison.OrdinalIgnoreCase);
        lock (gate)
        {
            var current = ForOwner(owner);
            if (!current.GroupIds.Contains(group)) return false;
            var day = DateOnly.FromDateTime(sentAt.UtcDateTime);
            if (!current.People.TryGetValue(sender, out var person))
            {
                if (current.People.Count >= MaximumPeople)
                {
                    var oldest = current.People.MinBy(pair => pair.Value.LastSeen);
                    current.People.Remove(oldest.Key);
                }
                current.People[sender] = person = new();
            }
            if (!person.Days.TryGetValue(group, out var days)) person.Days[group] = days = [];
            bool changed = days.Add(day);
            if (asked && (!person.AskedAt.TryGetValue(group, out var previous) || previous < sentAt))
            {
                person.AskedAt[group] = sentAt;
                changed = true;
            }
            if (changed)
            {
                person.Name = name.Length > 80 ? name[..80] : name;
                person.LastSeen = now;
                Prune(current, now);
                store.Write(file, current);
            }
        }
        return asked;
    }

    private static bool Prune(RegularState current, DateTimeOffset now)
    {
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1 - WindowDays);
        bool changed = false;
        foreach (var person in current.People.Values)
        {
            foreach (var days in person.Days.Values) changed |= days.RemoveWhere(day => day < cutoff) > 0;
            foreach (var group in person.AskedAt.Where(pair => pair.Value < now.AddDays(-WindowDays)).Select(pair => pair.Key).ToArray())
                changed |= person.AskedAt.Remove(group);
        }
        foreach (var sender in current.People.Where(pair => pair.Value.LastSeen < now.AddDays(-WindowDays)).Select(pair => pair.Key).ToArray())
            changed |= current.People.Remove(sender);
        return changed;
    }

    public RegularSuggestion[] Suggestions(string owner, IReadOnlySet<string> joined, DateTimeOffset now)
    {
        lock (gate)
        {
            var current = ForOwner(owner);
            if (Prune(current, now)) store.Write(file, current);
            return current.People.Select(pair => Suggestion(current, pair.Key, joined, now))
                .OfType<RegularSuggestion>().OrderByDescending(person => person.ActiveDays).ThenBy(person => person.SteamId)
                .ToArray();
        }
    }

    private static RegularSuggestion? Suggestion(RegularState current, ulong sender, IReadOnlySet<string> joined, DateTimeOffset now)
    {
        if (!current.People.TryGetValue(sender, out var person) || person.DismissedUntil > now) return null;
        return person.Days.Where(pair => pair.Value.Count >= 3 && joined.Contains(pair.Key)
                && current.GroupIds.Contains(pair.Key) && person.AskedAt.GetValueOrDefault(pair.Key) >= now.AddDays(-WindowDays))
            .OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new RegularSuggestion(sender.ToString(CultureInfo.InvariantCulture), person.Name, pair.Key, pair.Value.Count))
            .FirstOrDefault();
    }

    public void Act(string owner, ulong sender, IReadOnlySet<string> joined, DateTimeOffset now, bool request, Action send)
    {
        lock (gate)
        {
            var current = ForOwner(owner);
            Prune(current, now);
            if (Suggestion(current, sender, joined, now) is null)
                throw new ArgumentException("This suggestion is no longer eligible. Refresh Group Regulars.");
            if (request && current.LastRequestAt > now.AddHours(-24))
                throw new ArgumentException("Group Regulars can send one friend request per account every 24 hours.");

            current.People[sender].DismissedUntil = now.AddDays(WindowDays);
            if (request) current.LastRequestAt = now;
            // Reserve before contacting Steam so concurrent clicks and uncertain delivery cannot send twice.
            store.Write(file, current);
            if (request) send();
        }
    }
}
