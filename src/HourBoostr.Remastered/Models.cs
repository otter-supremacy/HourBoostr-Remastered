using System.Text.Json;
using System.Text.Json.Serialization;

namespace HourBoostr.Remastered;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public List<AccountSettings> Accounts { get; set; } = [];
    public string? ReleaseRepository { get; set; }
    public List<string> ProtectedLoginNames { get; set; } = [];
}

public sealed class AccountSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "New account";
    public string LoginName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Paused { get; set; }
    public string Presence { get; set; } = "online";
    public string? PresenceBadge { get; set; }
    public uint? PreferredAppId { get; set; }
    public ProfileDisplaySettings ProfileDisplay { get; set; } = new();
    public List<uint> SelectedAppIds { get; set; } = [];
    public List<uint> ExcludedAppIds { get; set; } = [];
    public int RandomCount { get; set; } = 10;
    public int RotationMinutes { get; set; } = 60;
    public string SelectionMode { get; set; } = "manual";
    public bool PauseWhilePlaying { get; set; } = true;
    public bool AutoAcceptFriendRequests { get; set; }
    public bool FriendWelcomeEnabled { get; set; }
    public string FriendWelcomeMessage { get; set; } = "Thanks for adding me! I am AFK right now and will respond later.";
    public AutoJoinSettings AutoJoinGroups { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();
    public AfkSettings Afk { get; set; } = new();
    public GroupReplySettings GroupReplies { get; set; } = new();
    public AiReplySettings AiReplies { get; set; } = new();
    public Dictionary<uint, double> TargetHours { get; set; } = [];

    public void Validate()
    {
        if (Label is null || LoginName is null || Schedule is null || Afk is null || SelectedAppIds is null || ExcludedAppIds is null || TargetHours is null) throw new ArgumentException("Account settings are incomplete.");
        if (!Guid.TryParseExact(Id, "N", out _)) throw new ArgumentException("Invalid account ID.");
        Label = Label.Trim(); LoginName = LoginName.Trim();
        if (FriendWelcomeMessage is null || FriendWelcomeMessage.Length > 500 || (FriendWelcomeEnabled && string.IsNullOrWhiteSpace(FriendWelcomeMessage))) throw new ArgumentException("Friend welcome message must be 1–500 characters when enabled.");
        if (Label.Length is < 1 or > 80) throw new ArgumentException("Account label must be 1–80 characters.");
        if (LoginName.Length > 100) throw new ArgumentException("Login name is too long.");
        if (!SteamAppearance.IsPresence(Presence)) throw new ArgumentException("Choose online, busy, away, or invisible.");
        if (PresenceBadge is not null && !SteamAppearance.IsBadge(PresenceBadge)) throw new ArgumentException("Choose a supported Steam display badge.");
        if (SelectionMode is not ("manual" or "random" or "rotate")) throw new ArgumentException("Unknown selection mode.");
        if (RandomCount is < 1 or > 32) throw new ArgumentException("Game count must be between 1 and 32.");
        if (RotationMinutes is < 5 or > 10080) throw new ArgumentException("Rotation must be between 5 minutes and 7 days.");
        if (SelectedAppIds.Count > 10000 || ExcludedAppIds.Count > 10000) throw new ArgumentException("Too many game IDs.");
        SelectedAppIds = SelectedAppIds.Where(x => x > 0).Distinct().ToList();
        ExcludedAppIds = ExcludedAppIds.Where(x => x > 0).Distinct().ToList();
        if (TargetHours.Any(x => !double.IsFinite(x.Value) || x.Value <= 0 || x.Value > 1000000)) throw new ArgumentException("Hour targets must be positive finite numbers.");
        if (AutoJoinGroups is null || AutoJoinGroups.DailyLimit is < 1 or > 25)
            throw new ArgumentException("The automatic group join limit must be between 1 and 25 per UTC day.");
        Schedule.Validate(); Afk.Validate();
        if (GroupReplies is null) throw new ArgumentException("Group reply settings are incomplete.");
        GroupReplies.Validate();
        if (ProfileDisplay is null) throw new ArgumentException("Profile display settings are incomplete.");
        ProfileDisplay.Validate();
        if (AiReplies is null) throw new ArgumentException("AI reply settings are incomplete.");
        AiReplies.Validate();
    }
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    public string TimeZone { get; set; } = TimeZoneInfo.Local.Id;
    public string Start { get; set; } = "00:00";
    public string End { get; set; } = "00:00";
    public List<DayOfWeek> Days { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
    public void Validate()
    {
        if (Days is null || TimeZone is null) throw new ArgumentException("Schedule settings are incomplete.");
        if (!TimeOnly.TryParseExact(Start, "HH:mm", out _) || !TimeOnly.TryParseExact(End, "HH:mm", out _)) throw new ArgumentException("Schedule times must use HH:mm.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("Unknown schedule time zone."); }
        if (Days.Any(x => !Enum.IsDefined(x))) throw new ArgumentException("Invalid day.");
    }
}

public sealed class ProfileDisplaySettings
{
    public string Mode { get; set; } = "default";
    public List<uint> AppIds { get; set; } = [];
    public string CustomText { get; set; } = "";
    public int IntervalMinutes { get; set; } = 60;
    public DateTimeOffset? AnchorUtc { get; set; }
    public void Validate()
    {
        if (Mode is not ("default" or "rotate" or "custom" or "mixed") || AppIds is null || CustomText is null) throw new ArgumentException("Invalid profile display settings.");
        AppIds = AppIds.Where(x => x > 0).Distinct().ToList();
        if (AppIds.Count > 100 || IntervalMinutes is < 5 or > 10080) throw new ArgumentException("Choose up to 100 display games and an interval of 5–10080 minutes.");
        if (CustomText.Length > 80 || System.Text.Encoding.UTF8.GetByteCount(CustomText) > 128 || CustomText.Any(char.IsControl)) throw new ArgumentException("Custom display must be a single line, up to 80 characters / 128 UTF-8 bytes.");
        if (Mode is "custom" or "mixed" && string.IsNullOrWhiteSpace(CustomText)) throw new ArgumentException("Enter custom display text.");
        if (Mode is "rotate" or "mixed" && AppIds.Count == 0) throw new ArgumentException("Choose games for the profile rotation.");
    }
}

public sealed class AiReplySettings
{
    public bool PrivateMessages { get; set; }
    public bool Groups { get; set; }
    public string Instructions { get; set; } = "Use casual, friendly language. Keep replies to one or two short sentences. Say you are an automated assistant replying while the account owner is away. Do not invent facts about the owner.";
    public string? QwenInstructions { get; set; }
    public int DailyLimit { get; set; } = 100;
    public int CooldownSeconds { get; set; } = 15;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Instructions) || Instructions.Length > 2000) throw new ArgumentException("AI instructions must be 1–2000 characters.");
        if (QwenInstructions is not null && (string.IsNullOrWhiteSpace(QwenInstructions) || QwenInstructions.Length > 2000))
            throw new ArgumentException("Qwen instructions must be 1–2000 characters.");
        if (DailyLimit is < 1 or > 500) throw new ArgumentException("AI daily limit must be 1–500 requests per model per account.");
        if (CooldownSeconds is < 2 or > 3600) throw new ArgumentException("AI cooldown must be 2–3600 seconds.");
    }
}

public sealed class GroupReplySettings
{
    public bool Enabled { get; set; }
    public List<string> Messages { get; set; } = ["", "", ""];
    public int CooldownSeconds { get; set; } = 5;
    public void Validate()
    {
        if (Messages is null || Messages.Count != 3 || Messages.Any(x => x is null || x.Length > 500 || (Enabled && string.IsNullOrWhiteSpace(x))))
            throw new ArgumentException("Enter three group replies of 1–500 characters before enabling.");
        if (CooldownSeconds is < 1 or > 3600) throw new ArgumentException("Group reply cooldown must be 1–3600 seconds.");
    }
}

public sealed class AfkSettings
{
    public bool Enabled { get; set; }
    public string Message { get; set; } = "I am AFK right now and will respond later.";
    public int CooldownMinutes { get; set; } = 10;
    public List<string> ExcludedFriends { get; set; } = [];
    public ScheduleSettings QuietHours { get; set; } = new() { Start = "23:00", End = "07:00" };
    public void Validate()
    {
        if (Message is null || QuietHours is null || ExcludedFriends is null || ExcludedFriends.Any(x => !ulong.TryParse(x, out _))) throw new ArgumentException("Invalid AFK settings or excluded friend IDs.");
        if (Message.Length is < 1 or > 500) throw new ArgumentException("AFK reply must be 1–500 characters.");
        if (CooldownMinutes is < 1 or > 1440) throw new ArgumentException("AFK cooldown must be 1–1440 minutes.");
        QuietHours.Validate();
    }
}

public sealed class LibraryGame
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public uint SteamMinutes { get; set; }
    public bool SteamHoursReported { get; set; } = true;
    public double EstimatedTotalSeconds { get; set; }
    public double IdleSeconds { get; set; }
}

public sealed class AccountStatistics
{
    public Dictionary<uint, LibraryGame> Games { get; set; } = [];
    public Dictionary<string, Dictionary<uint, double>> DailySeconds { get; set; } = [];
    public DateTimeOffset? LastLibrarySync { get; set; }
    public double SessionSeconds { get; set; }
}

public sealed record LogEntry(DateTimeOffset At, string Level, string? AccountId, string Message);
public sealed record SessionSecret(string LoginName, string RefreshToken, string? GuardData);
public sealed record LoginRequest(string Method, string? LoginName, string? Password);
public sealed record GuardRequest(string Code);
public sealed record CreateAccountRequest(string Label);
public sealed record PairRequest(string Key);
public sealed record SelectionRequest(List<uint> AppIds);
public sealed record GameTargetRequest(uint AppId, double? Hours);
public sealed record UpdateStageRequest(string PackagePath, string Sha256);

public sealed class AccountView
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string State { get; set; } = "signed_out";
    public string Detail { get; set; } = "Sign in to load your library.";
    public bool Connected { get; set; }
    public bool LoggedIn { get; set; }
    public bool HasSavedSession { get; set; }
    public bool PlayingElsewhere { get; set; }
    public string? SteamId { get; set; }
    public string? PersonaName { get; set; }
    public int LibraryCount { get; set; }
    public List<uint> PlayingAppIds { get; set; } = [];
    public List<string> PlayingNames { get; set; } = [];
    public DateTimeOffset? LastSteamResponse { get; set; }
    public DateTimeOffset? LastLibrarySync { get; set; }
    public string? ReportedPresence { get; set; }
    public DateTimeOffset? PresenceCheckedAt { get; set; }
    public double SessionSeconds { get; set; }
    public double TotalIdleSeconds { get; set; }
    public int Reconnects { get; set; }
    public int ConsecutiveProbeFailures { get; set; }
    public DateTimeOffset? ConnectedSince { get; set; }
    public DateTimeOffset? LastDisconnectAt { get; set; }
    public string? LastDisconnectReason { get; set; }
    public DateTimeOffset? NextReconnectAt { get; set; }
    public string? RequestedProfileDisplay { get; set; }
    public string? FriendObservedGame { get; set; }
    public DateTimeOffset? NextProfileDisplayChange { get; set; }
    public string? AuthPrompt { get; set; }
    public string? QrSvg { get; set; }
    public GroupJoinActivity[] GroupJoinActivity { get; set; } = [];
}
