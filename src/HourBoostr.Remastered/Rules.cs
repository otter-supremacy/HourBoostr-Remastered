using System.Security.Cryptography;

namespace HourBoostr.Remastered;

public static class ScheduleRule
{
    public static bool Allows(ScheduleSettings schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled) return true;
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone));
        var current = TimeOnly.FromDateTime(local.DateTime);
        var start = TimeOnly.ParseExact(schedule.Start, "HH:mm");
        var end = TimeOnly.ParseExact(schedule.End, "HH:mm");
        if (start == end) return schedule.Days.Contains(local.DayOfWeek);
        if (start < end) return schedule.Days.Contains(local.DayOfWeek) && current >= start && current < end;
        var day = current < end ? local.AddDays(-1).DayOfWeek : local.DayOfWeek;
        return schedule.Days.Contains(day) && (current >= start || current < end);
    }
}

public static class GameSelection
{
    public const int SteamLimit = 32;
    public static List<uint> Choose(AccountSettings settings, IReadOnlyDictionary<uint, LibraryGame> games, IReadOnlyList<uint> current, bool rotate)
    {
        bool Eligible(uint id) => games.TryGetValue(id, out var game)
            && !settings.ExcludedAppIds.Contains(id)
            && (!settings.TargetHours.TryGetValue(id, out var target) || game.EstimatedTotalSeconds < target * 3600);
        var result = new List<uint>();
        if (settings.PreferredAppId is uint preferred && Eligible(preferred)) result.Add(preferred);
        var pool = (settings.SelectionMode == "manual" || settings.SelectionMode == "rotate" && settings.SelectedAppIds.Count > 0 ? settings.SelectedAppIds : games.Keys.ToList()).Where(Eligible).Where(id => !result.Contains(id)).Distinct().ToList();
        var count = settings.SelectionMode == "manual" ? SteamLimit : settings.RandomCount;
        if (settings.SelectionMode != "manual")
        {
            if (!rotate)
                foreach (var id in current.Where(Eligible).Where(id => pool.Contains(id)).Take(Math.Max(0, count - result.Count)))
                { result.Add(id); pool.Remove(id); }
            for (var i = pool.Count - 1; i > 0; i--) { var j = RandomNumberGenerator.GetInt32(i + 1); (pool[i], pool[j]) = (pool[j], pool[i]); }
        }
        result.AddRange(pool.Take(Math.Max(0, Math.Min(count, SteamLimit) - result.Count)));
        return result.Distinct().Take(SteamLimit).ToList();
    }
}

public sealed class ReplyGate
{
    private readonly Dictionary<ulong, DateTimeOffset> last = [];
    private readonly HashSet<ulong> pending = [];
    private readonly object gate = new();
    public bool TryReserve(ulong sender, ulong self, bool isFriend, bool localEcho, AfkSettings settings, DateTimeOffset now, int? aiCooldownSeconds = null)
    {
        if (!settings.Enabled || localEcho || sender == self || !isFriend || settings.ExcludedFriends.Contains(sender.ToString())) return false;
        if (settings.QuietHours.Enabled && ScheduleRule.Allows(settings.QuietHours, now)) return false;
        lock (gate)
        {
            if (pending.Contains(sender)) return false;
            foreach (var id in last.Where(x => now - x.Value > TimeSpan.FromDays(1)).Select(x => x.Key).ToList()) last.Remove(id);
            var cooldown = aiCooldownSeconds is int seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMinutes(settings.CooldownMinutes);
            if (last.TryGetValue(sender, out var previous) && now - previous < cooldown) return false;
            pending.Add(sender);
            last[sender] = now; return true;
        }
    }

    public void Complete(ulong sender, bool delivered)
    {
        lock (gate)
        {
            if (pending.Remove(sender) && !delivered) last.Remove(sender);
        }
    }

    public TimeSpan Remaining(ulong sender, TimeSpan duration, DateTimeOffset now)
    {
        lock (gate) return last.TryGetValue(sender, out var previous) && previous + duration > now
            ? previous + duration - now : TimeSpan.Zero;
    }
}

public static class HealthRule
{
    public static bool NeedsRecovery(AccountView view, DateTimeOffset now)
        // A failed unified-service query does not prove the CM connection died.
        // SteamKit owns CM heartbeats; retain recovery for inconsistent workers.
        => view.LoggedIn && !view.Connected && view.ConsecutiveProbeFailures >= 3 && view.LastSteamResponse is { } response && now - response > TimeSpan.FromMinutes(12);
}

public static class ReconnectPolicy
{
    public static int DelaySeconds(int failures) => Math.Min(160, 5 * (1 << Math.Clamp(failures, 1, 5)));
}
