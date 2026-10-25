using System.Diagnostics;
using System.Xml.Linq;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace HourBoostr.Remastered;

public sealed class SteamSession : IAsyncDisposable
{
    private readonly DataStore store;
    private readonly SecretVault vault;
    private readonly EventLog events;
    private readonly SteamClient client;
    private readonly SteamUser user;
    private readonly SteamFriends friends;
    private readonly SteamApps apps;
    private readonly Player player;
    private readonly FriendMessages messages;
    private readonly CallbackManager callbacks;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim networkCheck = new(1, 1);
    private readonly ReplyGate replyGate = new();
    private readonly FriendRequestGate friendRequestGate = new();
    private readonly HashSet<ulong> pendingFriends = new();
    private readonly HashSet<ulong> acceptingFriends = new();
    private readonly HashSet<string> protectedNames;
    private readonly AccountView view;
    private AccountStatistics statistics;
    private readonly string id;
    private Task? callbackPump;
    private string? password;
    private string method = "saved";
    private string loginName;
    private DashboardAuthenticator authenticator;
    private bool signingIn;
    private bool needsAuth;
    private bool disposed;
    private bool accountInfoReady;
    private DateTimeOffset reconnectAt = DateTimeOffset.MaxValue;
    private DateTimeOffset lastTick = DateTimeOffset.UtcNow;
    private DateTimeOffset nextProbe = DateTimeOffset.MaxValue;
    private DateTimeOffset nextSave = DateTimeOffset.UtcNow.AddMinutes(1);
    private DateTimeOffset nextPresence = DateTimeOffset.MinValue;
    private DateTimeOffset lastRotation = DateTimeOffset.UtcNow;
    private DateTimeOffset nextReassert = DateTimeOffset.MinValue;
    private string? lastPresence;
    private List<uint> chosen = [];
    public DateTimeOffset LastPumpUtc { get; private set; } = DateTimeOffset.UtcNow;

    public SteamSession(AccountSettings config, DataStore store, SecretVault vault, EventLog events, IEnumerable<string> protectedNames, LoginRequest? login = null)
    {
        this.store = store; this.vault = vault; this.events = events; id = config.Id;
        this.protectedNames = new(protectedNames, StringComparer.OrdinalIgnoreCase);
        loginName = login?.LoginName?.Trim() ?? config.LoginName;
        if (this.protectedNames.Contains(loginName)) throw new ArgumentException("This account is protected from use in this installation.");
        if (login is not null) { method = login.Method; password = login.Password; }
        statistics = store.Read<AccountStatistics>($"statistics-{id}.json") ?? new();
        statistics.SessionSeconds = 0;
        client = new SteamClient(SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.WebSocket | ProtocolTypes.Tcp)));
        callbacks = new(client);
        user = client.GetHandler<SteamUser>()!; friends = client.GetHandler<SteamFriends>()!; apps = client.GetHandler<SteamApps>()!;
        var unified = client.GetHandler<SteamUnifiedMessages>()!;
        player = unified.CreateService<Player>(); messages = unified.CreateService<FriendMessages>();
        view = new() { Id = id, Label = config.Label, HasSavedSession = vault.Get(id) is not null, LibraryCount = statistics.Games.Count };
        authenticator = new((state, message) => Change(state, message), stop.Token);
        callbacks.Subscribe<SteamClient.ConnectedCallback>(callback => { _ = ConnectedAsync(); });
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(Disconnected);
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(LoggedOn);
        callbacks.Subscribe<SteamUser.LoggedOffCallback>(callback =>
        {
            if (callback.Result != EResult.OK) { lock (gate) view.LoggedIn = false; client.Disconnect(); }
        });
        callbacks.Subscribe<SteamUser.AccountInfoCallback>(_ => { accountInfoReady = true; ApplyPresence(); });
        callbacks.Subscribe<SteamUser.PlayingSessionStateCallback>(callback => { lock (gate) view.PlayingElsewhere = callback.PlayingBlocked; });
        callbacks.SubscribeServiceNotification<FriendMessagesClient, CFriendMessages_IncomingMessage_Notification>(notification => _ = ReplyAsync(notification.Body.steamid_friend, notification.Body.chat_entry_type, notification.Body.local_echo));
        callbacks.Subscribe<SteamFriends.FriendMsgCallback>(message => _ = ReplyAsync(message.Sender.ConvertToUInt64(), (int)message.EntryType, false));
        callbacks.Subscribe<SteamFriends.FriendsListCallback>(callback =>
        {
            lock (gate)
            {
                if (!callback.Incremental) pendingFriends.Clear();
                foreach (var friend in callback.FriendList)
                {
                    var steamId = friend.SteamID.ConvertToUInt64();
                    if (friend.SteamID.AccountType == EAccountType.Individual && friend.Relationship == EFriendRelationship.RequestRecipient) pendingFriends.Add(steamId);
                    else pendingFriends.Remove(steamId);
                }
            }
        });
        callbacks.Subscribe<SteamFriends.FriendAddedCallback>(callback =>
        {
            lock (gate)
            {
                if (!acceptingFriends.Remove(callback.SteamID.ConvertToUInt64())) return;
                if (callback.Result == EResult.OK) pendingFriends.Remove(callback.SteamID.ConvertToUInt64());
            }
            events.Add(callback.Result == EResult.OK ? "info" : "warning", id, callback.Result == EResult.OK ? "Accepted an incoming friend request." : "Steam could not accept an incoming friend request: " + callback.Result);
        });
    }

    public void Start()
    {
        callbackPump = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); LastPumpUtc = DateTimeOffset.UtcNow; }
                catch (Exception error) { events.Add("warning", id, "Steam callback processing failed: " + error.GetType().Name); }
            }
        });
        if (method != "saved" || vault.Get(id) is not null) Connect();
    }
    public AccountView Snapshot()
    {
        lock (gate)
        {
            view.Label = store.Account(id)?.Label ?? view.Label;
            view.LibraryCount = statistics.Games.Count; view.SessionSeconds = statistics.SessionSeconds;
            view.TotalIdleSeconds = statistics.Games.Values.Sum(x => x.IdleSeconds);
            view.LastLibrarySync = statistics.LastLibrarySync;
            return JsonDefaults.Clone(view);
        }
    }
    public AccountStatistics Statistics() { lock (gate) return JsonDefaults.Clone(statistics); }
    public bool SubmitGuard(string code) => authenticator.Submit(code);
    public void Reselect() { lock (gate) { chosen = []; lastRotation = DateTimeOffset.MinValue; } }
    private void Change(string state, string message)
    {
        lock (gate) { view.State = state; view.Detail = message; view.AuthPrompt = state.StartsWith("waiting_") ? message : null; }
    }
    private void Connect()
    {
        if (disposed || stop.IsCancellationRequested || needsAuth) return;
        reconnectAt = DateTimeOffset.MaxValue; accountInfoReady = false; lastPresence = null;
        Change("connecting", "Connecting to Steam…"); client.Connect();
    }
    private async Task ConnectedAsync()
    {
        if (signingIn || stop.IsCancellationRequested) return;
        signingIn = true;
        lock (gate) view.Connected = true;
        try
        {
            var saved = method == "saved" ? vault.Get(id) : null;
            SessionSecret secret;
            if (saved is not null) secret = saved;
            else
            {
                if (method == "saved") { AuthenticationRequired("No saved session. Sign in to continue."); return; }
                var details = new AuthSessionDetails { IsPersistentSession = true, Authenticator = authenticator, DeviceFriendlyName = "HourBoostr Remastered by Otter" };
                AuthPollResult result;
                if (method == "qr")
                {
                    var session = await client.Authentication.BeginAuthSessionViaQRAsync(details);
                    void RefreshQr()
                    {
                        using var generator = new QRCodeGenerator();
                        using var data = generator.CreateQrCode(session.ChallengeURL, QRCodeGenerator.ECCLevel.M);
                        using var qr = new SvgQRCode(data);
                        lock (gate) view.QrSvg = qr.GetGraphic(5);
                        Change("waiting_qr", "Scan this QR code using Steam Mobile and approve the sign-in.");
                    }
                    session.ChallengeURLChanged = RefreshQr; RefreshQr();
                    result = await session.PollingWaitForResultAsync(stop.Token);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrEmpty(password)) throw new ArgumentException("Enter your Steam login name and password.");
                    Change("authenticating", "Submitting your sign-in to Steam…");
                    details.Username = loginName; details.Password = password;
                    details.GuardData = vault.Get(id)?.GuardData;
                    var session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(details);
                    result = await session.PollingWaitForResultAsync(stop.Token);
                }
                stop.Token.ThrowIfCancellationRequested();
                if (protectedNames.Contains(result.AccountName)) throw new ArgumentException("This account is protected from use in this installation.");
                secret = new(result.AccountName, result.RefreshToken, result.NewGuardData);
                lock (gate)
                {
                    if (disposed) return;
                    stop.Token.ThrowIfCancellationRequested();
                    vault.Save(id, secret);
                    var config = store.Account(id) ?? throw new KeyNotFoundException("Account was removed during sign-in.");
                    config.LoginName = result.AccountName; store.Save(config); method = "saved";
                }
            }
            password = null; loginName = secret.LoginName;
            lock (gate) { view.QrSvg = null; view.AuthPrompt = null; view.HasSavedSession = true; }
            Change("authenticating", "Opening your Steam session…");
            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = secret.LoginName, AccessToken = secret.RefreshToken,
                ShouldRememberPassword = true, ChatMode = SteamUser.ChatMode.NewSteamChat,
                LoginID = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue)
            });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (AuthenticationException error)
        {
            AuthenticationRequired(error.Result switch
            {
                EResult.InvalidPassword => "Steam rejected these credentials. Check the account login name, which may differ from your profile name.",
                EResult.RateLimitExceeded => "Steam is limiting sign-in attempts. Wait before trying again.",
                _ => "Steam authentication returned " + error.Result + ". Start a new sign-in."
            });
        }
        catch (TimeoutException) { AuthenticationRequired("The sign-in timed out. Start a new sign-in."); }
        catch (Exception error) { AuthenticationRequired(error is ArgumentException ? error.Message : "Sign-in stopped: " + error.GetType().Name + ". Try again."); }
        finally { password = null; signingIn = false; }
    }
    private void AuthenticationRequired(string message)
    {
        needsAuth = true; password = null;
        lock (gate) { view.LoggedIn = false; view.QrSvg = null; view.AuthPrompt = null; }
        Change("auth_required", message); events.Add("warning", id, message); client.Disconnect();
    }
    private void LoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result != EResult.OK)
        {
            if (callback.Result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired)
            { vault.Forget(id); lock (gate) view.HasSavedSession = false; AuthenticationRequired("Your saved Steam session is no longer accepted. Sign in again."); }
            else { events.Add("warning", id, "Steam session returned " + callback.Result); client.Disconnect(); }
            return;
        }
        lock (gate)
        {
            view.LoggedIn = true; view.Connected = true; view.LastSteamResponse = DateTimeOffset.UtcNow;
            view.SteamId = client.SteamID!.ConvertToUInt64().ToString(); view.PlayingElsewhere = false;
        }
        lastTick = DateTimeOffset.UtcNow; nextProbe = DateTimeOffset.UtcNow.AddMinutes(5);
        Change("online", "Signed in. Loading your owned games…"); events.Add("info", id, "Steam sign-in succeeded.");
        _ = RefreshLibraryAsync();
    }
    private void Disconnected(SteamClient.DisconnectedCallback callback)
    {
        lock (gate) { view.Connected = false; view.LoggedIn = false; view.PlayingAppIds = []; view.PlayingNames = []; }
        if (disposed || stop.IsCancellationRequested || needsAuth) return;
        if (vault.Get(id) is null) { AuthenticationRequired("Connection interrupted. Start a new sign-in."); return; }
        lock (gate) view.Reconnects++;
        int delay = Math.Min(300, 5 * (1 << Math.Min(view.Reconnects, 5))) + Random.Shared.Next(0, 5);
        reconnectAt = DateTimeOffset.UtcNow.AddSeconds(delay);
        Change("reconnecting", $"Connection lost. Retrying in {delay} seconds."); events.Add("warning", id, "Steam disconnected; reconnection scheduled.");
    }
    private void ApplyPresence()
    {
        if (!accountInfoReady || !view.LoggedIn) return;
        var presence = store.Account(id)?.Presence ?? "online";
        if (lastPresence == presence) return;
        friends.SetPersonaState(presence switch { "away" => EPersonaState.Away, "invisible" => EPersonaState.Invisible, _ => EPersonaState.Online });
        lastPresence = presence;
        lock (gate) view.PersonaName = friends.GetPersonaName();
    }
    public async Task RefreshLibraryAsync()
    {
        if (!view.LoggedIn || !await networkCheck.WaitAsync(0, stop.Token)) return;
        try
        {
            var result = await player.GetOwnedGames(new CPlayer_GetOwnedGames_Request
            {
                steamid = client.SteamID!.ConvertToUInt64(), include_appinfo = true,
                include_free_sub = true, include_played_free_games = true, skip_unvetted_apps = false
            });
            if (result.Result != EResult.OK) throw new InvalidOperationException(result.Result.ToString());
            lock (gate)
            {
                var library = new Dictionary<uint, LibraryGame>();
                foreach (var game in result.Body.games.Where(x => x.appid > 0))
                {
                    var appId = (uint)game.appid;
                    var record = statistics.Games.GetValueOrDefault(appId) ?? new LibraryGame { AppId = appId };
                    record.Name = game.name ?? $"App {appId}"; record.SteamMinutes = (uint)Math.Max(0, game.playtime_forever);
                    record.EstimatedTotalSeconds = Math.Max(record.EstimatedTotalSeconds, record.SteamMinutes * 60d); library[appId] = record;
                }
                statistics.Games = library; statistics.LastLibrarySync = DateTimeOffset.UtcNow; view.LastSteamResponse = DateTimeOffset.UtcNow;
                view.LibraryCount = library.Count;
                store.Write($"statistics-{id}.json", statistics);
            }
            events.Add("info", id, $"Loaded {statistics.Games.Count} owned games.");
            nextReassert = DateTimeOffset.MinValue;
        }
        catch (Exception error) when (!stop.IsCancellationRequested) { events.Add("warning", id, "Library refresh failed: " + error.GetType().Name + ". Cached games remain available."); }
        finally { networkCheck.Release(); }
    }
    public async Task TickAsync(DateTimeOffset now)
    {
        if (disposed) return;
        var config = store.Account(id); if (config is null) return;
        if (!client.IsConnected && !needsAuth && now >= reconnectAt) Connect();
        var elapsed = Math.Clamp((now - lastTick).TotalSeconds, 0, 10); lastTick = now;
        lock (gate)
        {
            if (view.LoggedIn && view.PlayingAppIds.Count > 0 && !view.PlayingElsewhere)
            {
                statistics.SessionSeconds += elapsed;
                var date = now.UtcDateTime.ToString("yyyy-MM-dd");
                if (!statistics.DailySeconds.TryGetValue(date, out var day)) statistics.DailySeconds[date] = day = [];
                foreach (var appId in view.PlayingAppIds)
                    if (statistics.Games.TryGetValue(appId, out var game))
                    { game.IdleSeconds += elapsed; game.EstimatedTotalSeconds += elapsed; day[appId] = day.GetValueOrDefault(appId) + elapsed; }
            }
        }
        if (!view.LoggedIn) return;
        AcceptIncomingFriendRequests(config, now);
        ApplyPresence();
        var allowed = config.Enabled && !config.Paused && ScheduleRule.Allows(config.Schedule, now) && !view.PlayingElsewhere;
        lock (gate)
        {
            var rotate = config.SelectionMode == "rotate" && now - lastRotation >= TimeSpan.FromMinutes(config.RotationMinutes);
            chosen = GameSelection.Choose(config, statistics.Games, chosen, rotate);
            if (rotate) lastRotation = now;
        }
        var playing = allowed ? chosen : [];
        var state = !config.Enabled || config.Paused ? "paused" : !ScheduleRule.Allows(config.Schedule, now) ? "scheduled" : view.PlayingElsewhere ? "playing_elsewhere" : playing.Count == 0 ? "idle" : "idling";
        Change(state, state switch
        {
            "paused" => "Paused by you.", "scheduled" => "Outside the configured schedule.",
            "playing_elsewhere" => "Paused while this account is playing elsewhere.",
            "idle" => "Choose owned games, or review exclusions and hour targets.",
            _ => $"Reporting {playing.Count} game(s) as playing."
        });
        if (!playing.SequenceEqual(view.PlayingAppIds) || now >= nextReassert)
        {
            var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
            foreach (var appId in playing) message.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(appId) });
            if (!view.PlayingElsewhere) client.Send(message);
            lock (gate) { view.PlayingAppIds = [.. playing]; view.PlayingNames = playing.Select(x => statistics.Games.GetValueOrDefault(x)?.Name ?? $"App {x}").ToList(); }
            nextReassert = now.AddMinutes(15);
            nextPresence = now.AddSeconds(5);
        }
        if (now >= nextProbe) { nextProbe = now.AddMinutes(5); _ = ProbeAsync(); }
        if (now >= nextPresence) { nextPresence = now.AddMinutes(5); _ = CheckReportedPresenceAsync(); }
        if (now >= nextSave)
        {
            nextSave = now.AddMinutes(1);
            lock (gate)
            {
                foreach (var date in statistics.DailySeconds.Keys.OrderDescending().Skip(90).ToList()) statistics.DailySeconds.Remove(date);
                store.Write($"statistics-{id}.json", statistics);
            }
        }
        await Task.CompletedTask;
    }
    private void AcceptIncomingFriendRequests(AccountSettings config, DateTimeOffset now)
    {
        if (!config.Enabled || !config.AutoAcceptFriendRequests || client.SteamID is null) return;
        lock (gate)
        {
            foreach (var value in pendingFriends.ToArray())
            {
                var requester = new SteamID(value);
                var relationship = friends.GetFriendRelationship(requester);
                if (relationship != EFriendRelationship.RequestRecipient) { pendingFriends.Remove(value); continue; }
                if (!friendRequestGate.TryReserve(true, value, client.SteamID.ConvertToUInt64(), requester.AccountType, relationship, now)) continue;
                acceptingFriends.Add(value);
                try { friends.AddFriend(requester); }
                catch (Exception error) { acceptingFriends.Remove(value); events.Add("warning", id, "Friend request acceptance failed: " + error.GetType().Name); }
                break;
            }
        }
    }
    private async Task ProbeAsync()
    {
        if (!await networkCheck.WaitAsync(0, stop.Token)) return;
        try
        {
            var request = new CPlayer_GetOwnedGames_Request { steamid = client.SteamID!.ConvertToUInt64(), include_appinfo = false, include_played_free_games = true };
            if (statistics.Games.Count > 0) request.appids_filter.Add(statistics.Games.Keys.First());
            var result = await player.GetOwnedGames(request);
            if (result.Result == EResult.OK) { lock (gate) view.LastSteamResponse = DateTimeOffset.UtcNow; }
            else events.Add("warning", id, "Steam health check returned " + result.Result);
        }
        catch (Exception error) when (!stop.IsCancellationRequested) { events.Add("warning", id, "Steam health check failed: " + error.GetType().Name); }
        finally { networkCheck.Release(); }
    }
    private async Task CheckReportedPresenceAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var xml = await http.GetStringAsync($"https://steamcommunity.com/profiles/{client.SteamID!.ConvertToUInt64()}/?xml=1", stop.Token);
            var document = XDocument.Parse(xml);
            var state = document.Root?.Element("stateMessage")?.Value;
            lock (gate) { view.ReportedPresence = state is null ? "Not available for this profile" : System.Net.WebUtility.HtmlDecode(state.Replace("<br/>", " · ").Replace("<br>", " · ")); view.PresenceCheckedAt = DateTimeOffset.UtcNow; }
        }
        catch (Exception) when (!stop.IsCancellationRequested) { lock (gate) { view.ReportedPresence = "Profile presence could not be checked"; view.PresenceCheckedAt = DateTimeOffset.UtcNow; } }
    }
    private async Task ReplyAsync(ulong sender, int entryType, bool localEcho)
    {
        if (entryType != (int)EChatEntryType.ChatMsg || client.SteamID is null) return;
        var config = store.Account(id); if (config is null) return;
        if (!replyGate.TryReserve(sender, client.SteamID.ConvertToUInt64(), friends.GetFriendRelationship(new SteamID(sender)) == EFriendRelationship.Friend, localEcho, config.Afk, DateTimeOffset.UtcNow)) return;
        try
        {
            var result = await messages.SendMessage(new CFriendMessages_SendMessage_Request { steamid = sender, chat_entry_type = entryType, message = config.Afk.Message, contains_bbcode = false, echo_to_sender = false });
            if (result.Result != EResult.OK) throw new InvalidOperationException();
            events.Add("info", id, "Sent the configured AFK reply to an incoming private friend message.");
        }
        catch (Exception) { replyGate.Release(sender); events.Add("warning", id, "AFK reply could not be delivered."); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; password = null; stop.Cancel();
        client.Disconnect();
        if (callbackPump is not null) await callbackPump.WaitAsync(TimeSpan.FromSeconds(3));
        lock (gate) store.Write($"statistics-{id}.json", statistics);
        stop.Dispose();
    }
}
