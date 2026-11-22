using SteamKit2;
using SteamKit2.Internal;

namespace HourBoostr.Remastered;

public sealed record ProfileDisplayPlan(List<uint> SteamGames, string? CustomText, string? Label, DateTimeOffset? NextChange);
public static class ProfileDisplayRule
{
    public static ProfileDisplayPlan Choose(AccountSettings config, IReadOnlyDictionary<uint, LibraryGame> library, IReadOnlyList<uint> chosen, DateTimeOffset now, bool allowed)
    {
        if (!allowed) return new([], null, null, null);
        var display = config.ProfileDisplay;
        var entries = new List<(uint? Id, string? Text)>();
        if (display.Mode is "rotate" or "mixed")
            entries.AddRange(display.AppIds.Where(id => library.TryGetValue(id, out var g) && !config.ExcludedAppIds.Contains(id) && (!config.TargetHours.TryGetValue(id, out var target) || g.EstimatedTotalSeconds < target * 3600)).Select(id => ((uint?)id, (string?)null)));
        if (display.Mode is "custom" or "mixed" && !string.IsNullOrWhiteSpace(display.CustomText)) entries.Add((null, display.CustomText));
        if (entries.Count == 0) return new(chosen.ToList(), null, chosen.Count > 0 ? library.GetValueOrDefault(chosen[0])?.Name : null, null);
        var anchor = display.AnchorUtc ?? now;
        var step = (long)Math.Max(0, Math.Floor((now - anchor).TotalMinutes / display.IntervalMinutes));
        var entry = entries[(int)(step % entries.Count)];
        DateTimeOffset? next = entries.Count > 1 ? anchor.AddMinutes((step + 1) * display.IntervalMinutes) : null;
        if (entry.Id is uint id) return new(new[] { id }.Concat(chosen).Distinct().Take(GameSelection.SteamLimit).ToList(), null, library[id].Name, next);
        return new(chosen.Take(GameSelection.SteamLimit - 1).ToList(), entry.Text, entry.Text, next);
    }
    public static ClientMsgProtobuf<CMsgClientGamesPlayed> Packet(ProfileDisplayPlan plan)
    {
        var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(plan.CustomText is null ? EMsg.ClientGamesPlayed : EMsg.ClientGamesPlayedWithDataBlob);
        if (plan.CustomText is not null)
            message.Body.games_played.Add(new() { game_id = new GameID { AppType = GameID.GameType.Shortcut, ModID = uint.MaxValue }, game_extra_info = plan.CustomText });
        foreach (var id in plan.SteamGames) message.Body.games_played.Add(new() { game_id = new GameID(id) });
        return message;
    }
}
