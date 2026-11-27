using System.Globalization;

namespace HourBoostr.Members;

internal sealed record ChatGroup(string Id, string Name);
internal sealed record ChatRegular(string SteamId, string GroupId, int ActiveDays);

internal sealed record MemberChatSettings
{
    public bool PrivateReplies { get; init; }
    public string PrivateMessage { get; init; } = "";
    public int PrivateCooldownMinutes { get; init; } = 15;
    public string[] ExcludedFriends { get; init; } = [];
    public bool QuietHours { get; init; }
    public string QuietStart { get; init; } = "23:00";
    public string QuietEnd { get; init; } = "07:00";
    public string TimeZone { get; init; } = "UTC";
    public string[] RegularGroups { get; init; } = [];
    public bool GroupReplies { get; init; }
    public string[] GroupMessages { get; init; } = ["", "", ""];
    public int GroupCooldownSeconds { get; init; } = 60;
    public bool AutoAccept { get; init; }
    public bool WelcomeEnabled { get; init; }
    public string WelcomeMessage { get; init; } = "";
    public bool AiPrivate { get; init; }
    public bool AiGroup { get; init; }

    public string GptPrompt { get; init; } = "";
    public string QwenPrompt { get; init; } = "";

    private static bool ReadTime(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public string? Validate(bool aiConfigured)
    {
        if (PrivateMessage is null || WelcomeMessage is null || GroupMessages is not { Length: 3 }
            || GroupMessages.Any(x => x is null || x.Length > 300)
            || PrivateMessage.Length > 300 || WelcomeMessage.Length > 300)
            return "Enter up to 300 characters in each message.";
        if ((PrivateReplies && string.IsNullOrWhiteSpace(PrivateMessage))
            || (GroupReplies && GroupMessages.Any(string.IsNullOrWhiteSpace))
            || (WelcomeEnabled && (!AutoAccept || string.IsNullOrWhiteSpace(WelcomeMessage))))
            return "Enter the messages for each enabled reply. Welcomes require automatic acceptance.";
        if (PrivateCooldownMinutes is < 1 or > 1440 || GroupCooldownSeconds is < 5 or > 3600)
            return "Choose a private cooldown of 1–1440 minutes and a group cooldown of 5–3600 seconds.";
        if (RegularGroups is not { Length: <= 10 } || RegularGroups.Any(x => !ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0))
            return "Choose up to 10 Steam groups for Group Regulars.";
        if (ExcludedFriends is not { Length: <= 30 } || ExcludedFriends.Any(x => !ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0))
            return "Enter up to 30 SteamID64 values for excluded friends.";
        if (!ReadTime(QuietStart, out var start) || !ReadTime(QuietEnd, out var end) || (QuietHours && start == end)
            || string.IsNullOrWhiteSpace(TimeZone) || TimeZone.Length > 80 || !TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _))
            return "Enter valid quiet hours and an IANA time zone, such as Asia/Kolkata.";
        if (GptPrompt is null || QwenPrompt is null || GptPrompt.Length > 1000 || QwenPrompt.Length > 1000)
            return "AI style prompts can contain up to 1,000 characters.";
        if ((AiPrivate && !PrivateReplies) || (AiGroup && !GroupReplies) || ((AiPrivate || AiGroup) && !aiConfigured))
            return "AI replies require the matching reply option and a configured Groq connection.";
        return null;
    }

    public bool AllowsPrivate(ulong sender, DateTimeOffset now)
    {
        if (!PrivateReplies || ExcludedFriends.Contains(sender.ToString(CultureInfo.InvariantCulture))) return false;
        if (!QuietHours) return true;
        if (!ReadTime(QuietStart, out var start) || !ReadTime(QuietEnd, out var end)
            || !TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone)) return false;
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var quiet = start < end ? local >= start && local < end : local >= start || local < end;
        return !quiet;
    }
}
