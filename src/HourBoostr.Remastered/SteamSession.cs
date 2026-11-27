using System.Diagnostics;
using System.Xml.Linq;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using CommunityClient = SteamKit2.WebUI.Internal.Community;
using SteamKit2.WebUI.Internal;

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
    private readonly ChatRoom groupChat;
    private readonly GroupReplyGate groupReplyGate = new();
    private readonly GroupPresetGuard groupPresetGuard;
    private readonly CommunityClient community;
    private readonly ProfileComments profileComments;
    private readonly SemaphoreSlim profileCommentPoll = new(1, 1);
    private DateTimeOffset nextProfileCommentCheck;
    private volatile string profileCommentStatus = "Waiting for Steam";
    private readonly GroupRegulars groupRegulars;
    private readonly GroupAutoJoin autoJoin;
    private readonly HashSet<ulong> pendingClans = [];
    private DateTimeOffset nextPendingClanSweep;
    private volatile RegularGroup[] regularGroups = [];
    private volatile bool friendsReady;
    private readonly SemaphoreSlim groupSync = new(1, 1);
    private DateTimeOffset nextGroupSync;
    private bool groupsActive;
    private int lastGroupCount = -1;
    private DateTimeOffset nextGroupDiagnostic;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> localAccounts = new();
    private readonly CallbackManager callbacks;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim healthCheck = new(1, 1);
    private int retryFailures;
    private string disconnectReason = "transport_interrupted";
    private DateTimeOffset retryNotBefore;
    private DateTimeOffset onlineSince;
    private readonly SemaphoreSlim networkCheck = new(1, 1);
    private static readonly AutomaticReplyTracker automaticReplies = new();
    private readonly ReplyGate aiReplyGate = new();
    private readonly PrivateReplyQueue aiReplyQueue = new();
    private readonly ReplyGate presetReplyGate = new();
    private readonly FriendRequestGate friendRequestGate = new();
    private readonly FriendWelcomeGate friendWelcomeGate;
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
    private (string Status, string Badge)? lastPresence;
    private string? lastCustomDisplay;
    private List<uint> chosen = [];
    public DateTimeOffset LastPumpUtc { get; private set; } = DateTimeOffset.UtcNow;

    public SteamSession(AccountSettings config, DataStore store, SecretVault vault, EventLog events, IEnumerable<string> protectedNames, LoginRequest? login = null)
    {
        this.store = store; this.vault = vault; this.events = events; id = config.Id;
        friendWelcomeGate = new(store, id);
        groupPresetGuard = new(store, id);
        groupRegulars = new(store, id);
        profileComments = new(store, id);
        this.protectedNames = new(protectedNames, StringComparer.OrdinalIgnoreCase);
        loginName = login?.LoginName?.Trim() ?? config.LoginName;
        if (this.protectedNames.Contains(loginName)) throw new ArgumentException("This account is protected from use in this installation.");
        if (login is not null) { method = login.Method; password = login.Password; }
        statistics = store.Read<AccountStatistics>($"statistics-{id}.json") ?? new();
        statistics.SessionSeconds = 0;
        client = new SteamClient(SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.WebSocket | ProtocolTypes.Tcp).WithConnectionTimeout(TimeSpan.FromSeconds(15))));
        callbacks = new(client);
        user = client.GetHandler<SteamUser>()!; friends = client.GetHandler<SteamFriends>()!; apps = client.GetHandler<SteamApps>()!;
        var unified = client.GetHandler<SteamUnifiedMessages>()!;
        player = unified.CreateService<Player>(); messages = unified.CreateService<FriendMessages>();
        groupChat = unified.CreateService<ChatRoom>();
        community = unified.CreateService<CommunityClient>();
        var groupTransport = new SteamGroupJoin(client, friends, groupChat, () => vault.Get(id)?.RefreshToken,
            owner => JoinSettings(owner) is { Enabled: true }, () => nextGroupSync = DateTimeOffset.MinValue);
        autoJoin = new(store, id, JoinSettings, groupTransport.Resolve,
            (message, count, token) => AiReplyService.For(store).InterpretGroupInviteAsync(id, message, count, token), stop.Token);
        callbacks.SubscribeServiceNotification<ChatRoomClient, CChatRoom_IncomingChatMessage_Notification>(notification => _ = ReplyToGroupAsync(notification.Body));
        callbacks.SubscribeServiceNotification<ChatRoomClient, CChatRoom_MemberStateChange_Notification>(notification =>
        {
            var invite = notification.Body;
            var self = client.SteamID;
            if (self is null || invite.chat_group_id == 0 || invite.member?.accountid != self.AccountID
                || invite.change != SteamKit2.Internal.EChatRoomMemberStateChange.k_EChatRoomMemberStateChange_Invited) return;
            var owner = self.ConvertToUInt64().ToString();
            if (GroupAutoJoin.Allows(JoinSettings(owner), JoinSource.Direct))
                autoJoin.Submit(owner, JoinSource.Direct, new(GroupKind.ChatInvite, invite.chat_group_id.ToString()));
        });
        view = new() { Id = id, Label = config.Label, HasSavedSession = vault.Get(id) is not null, LibraryCount = statistics.Games.Count };
        authenticator = new((state, message) => Change(state, message), stop.Token);
        callbacks.Subscribe<SteamClient.ConnectedCallback>(callback => { _ = ConnectedAsync(); });
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(Disconnected);
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(LoggedOn);
        callbacks.Subscribe<SteamUser.LoggedOffCallback>(callback =>
        {
            if (callback.Result != EResult.OK) { disconnectReason = callback.Result.ToString(); if (callback.Result == EResult.RateLimitExceeded) retryNotBefore = DateTimeOffset.UtcNow.AddMinutes(5); events.Add("warning", id, "Steam logged off: " + callback.Result); lock (gate) view.LoggedIn = false; client.Disconnect(); }
        });
        callbacks.Subscribe<SteamUser.AccountInfoCallback>(_ => { accountInfoReady = true; ApplyPresence(store.Account(id)); });
        callbacks.Subscribe<SteamUser.PlayingSessionStateCallback>(callback => { lock (gate) view.PlayingElsewhere = callback.PlayingBlocked; });
        callbacks.SubscribeServiceNotification<FriendMessagesClient, CFriendMessages_IncomingMessage_Notification>(notification => _ = ReplyAsync(notification.Body.steamid_friend, notification.Body.chat_entry_type, notification.Body.local_echo, notification.Body.message, notification.Body.message_no_bbcode, unified: true));
        callbacks.Subscribe<SteamFriends.FriendMsgCallback>(message => _ = ReplyAsync(message.Sender.ConvertToUInt64(), (int)message.EntryType, false, message.Message ?? ""));
        callbacks.Subscribe<SteamFriends.FriendsListCallback>(callback =>
        {
            lock (gate)
            {
                if (!callback.Incremental) { pendingFriends.Clear(); pendingClans.Clear(); friendsReady = true; }
                foreach (var friend in callback.FriendList)
                {
                    var steamId = friend.SteamID.ConvertToUInt64();
                    if (friend.SteamID.AccountType == EAccountType.Individual && friend.Relationship == EFriendRelationship.RequestRecipient) pendingFriends.Add(steamId);
                    else pendingFriends.Remove(steamId);
                    if (!GroupInvitation.IsClan(steamId)) continue;
                    if ((EClanRelationship)friend.Relationship == EClanRelationship.Invited) pendingClans.Add(steamId);
                    else pendingClans.Remove(steamId);
                }
            }
            if (callback.Incremental)
                QueueClanInvites(callback.FriendList.Where(entry => (EClanRelationship)entry.Relationship == EClanRelationship.Invited)
                    .Select(entry => entry.SteamID.ConvertToUInt64()).Where(GroupInvitation.IsClan));
            else QueuePendingClanInvites();
        });
        callbacks.Subscribe<SteamFriends.FriendAddedCallback>(callback =>
        {
            lock (gate)
            {
                if (!acceptingFriends.Remove(callback.SteamID.ConvertToUInt64())) return;
                if (callback.Result == EResult.OK) pendingFriends.Remove(callback.SteamID.ConvertToUInt64());
            }
            events.Add(callback.Result == EResult.OK ? "info" : "warning", id, callback.Result == EResult.OK ? "Accepted an incoming friend request." : "Steam could not accept an incoming friend request: " + callback.Result);
            if (callback.Result == EResult.OK) _ = SendFriendWelcomeAsync(callback.SteamID.ConvertToUInt64());
        });
    }

    private AutoJoinSettings? JoinSettings(string owner)
    {
        if (stop.IsCancellationRequested || !client.IsConnected || client.SteamID?.ConvertToUInt64().ToString() != owner)
            return null;
        lock (gate) if (!view.LoggedIn) return null;
        return store.Account(id) is { Enabled: true } config ? config.AutoJoinGroups : null;
    }

    private void QueueClanInvites(IEnumerable<ulong> invites)
    {
        var owner = client.SteamID?.ConvertToUInt64().ToString();
        if (owner is null || !GroupAutoJoin.Allows(JoinSettings(owner), JoinSource.Direct)) return;
        foreach (var invite in invites)
            autoJoin.Submit(owner, JoinSource.Direct, new(GroupKind.ClanInvite, invite.ToString()));
    }

    private void QueuePendingClanInvites()
    {
        ulong[] invites;
        lock (gate) invites = pendingClans.ToArray();
        QueueClanInvites(invites);
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
            view.GroupJoinActivity = autoJoin.History(view.SteamId);
            return JsonDefaults.Clone(view);
        }
    }
    public AccountStatistics Statistics() { lock (gate) return JsonDefaults.Clone(statistics); }
    public string? ObserveFriendGame(ulong steamId)
    {
        var target = new SteamID(steamId);
        if (!client.IsConnected || friends.GetFriendRelationship(target) != EFriendRelationship.Friend) return null;
        var title = friends.GetFriendGamePlayedName(target);
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }
    public bool SubmitGuard(string code) => authenticator.Submit(code);
    public void Reselect() { lock (gate) { chosen = []; lastRotation = DateTimeOffset.MinValue; } }
    private void Change(string state, string message)
    {
        lock (gate) { view.State = state; view.Detail = message; view.AuthPrompt = state.StartsWith("waiting_") ? message : null; }
    }
    private void Connect()
    {
        if (disposed || stop.IsCancellationRequested || needsAuth) return;
        if (DateTimeOffset.UtcNow < retryNotBefore) { reconnectAt = retryNotBefore; return; }
        reconnectAt = DateTimeOffset.MaxValue; accountInfoReady = false; lastPresence = null; lastCustomDisplay = null; nextReassert = DateTimeOffset.MinValue;
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
            else { disconnectReason = callback.Result.ToString(); if (callback.Result == EResult.RateLimitExceeded) retryNotBefore = DateTimeOffset.UtcNow.AddMinutes(5); events.Add("warning", id, "Steam session returned " + callback.Result); client.Disconnect(); }
            return;
        }
        lock (gate)
        {
            view.LoggedIn = true; view.Connected = true; view.LastSteamResponse = DateTimeOffset.UtcNow;
            view.ConsecutiveProbeFailures = 0; onlineSince = DateTimeOffset.UtcNow;
            view.ConnectedSince = onlineSince; view.NextReconnectAt = null;
            view.SteamId = client.SteamID!.ConvertToUInt64().ToString(); view.PlayingElsewhere = false;
        }
        lastTick = DateTimeOffset.UtcNow; nextProbe = DateTimeOffset.UtcNow.AddMinutes(5);
        localAccounts[client.SteamID!.ConvertToUInt64()] = id;
        nextGroupSync = DateTimeOffset.MinValue;
        Change("online", "Signed in. Loading your owned games…"); events.Add("info", id, "Steam sign-in succeeded.");
        _ = RefreshLibraryAsync();
    }
    private void Disconnected(SteamClient.DisconnectedCallback callback)
    {
        friendsReady = false;
        nextPendingClanSweep = DateTimeOffset.MinValue;
        lock (gate) pendingClans.Clear();
        regularGroups = [];
        lock (gate) { view.Connected = false; view.LoggedIn = false; view.PlayingAppIds = []; view.PlayingNames = []; }
        if (disposed || stop.IsCancellationRequested || needsAuth) return;
        if (vault.Get(id) is null) { AuthenticationRequired("Connection interrupted. Start a new sign-in."); return; }
        lock (gate) { view.Reconnects++; view.LastDisconnectAt = DateTimeOffset.UtcNow; view.LastDisconnectReason = disconnectReason; view.ConnectedSince = null; }
        if (onlineSince != default && DateTimeOffset.UtcNow - onlineSince >= TimeSpan.FromMinutes(10)) retryFailures = 0;
        onlineSince = default;
        int delay = ReconnectPolicy.DelaySeconds(++retryFailures) + Random.Shared.Next(0, 5);
        reconnectAt = DateTimeOffset.UtcNow.AddSeconds(delay);
        if (reconnectAt < retryNotBefore) reconnectAt = retryNotBefore;
        delay = Math.Max(1,(int)Math.Ceiling((reconnectAt-DateTimeOffset.UtcNow).TotalSeconds));
        lock (gate) view.NextReconnectAt = reconnectAt;
        disconnectReason = "transport_interrupted";
        Change("reconnecting", $"Connection lost. Retrying in {delay} seconds."); events.Add("warning", id, $"Steam disconnected; retry in {delay}s, failure streak {retryFailures}, initiated={callback.UserInitiated}.");
    }
    private void ApplyPresence(AccountSettings? config)
    {
        if (!accountInfoReady || !view.LoggedIn) return;
        var presence = (Status: config?.Presence ?? "online", Badge: config?.PresenceBadge ?? "normal");
        if (lastPresence == presence) return;
        client.Send(SteamAppearance.Packet(presence.Status, presence.Badge));
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
            var verifiedMissing = new List<uint>();
            var returnedIds = result.Body.games.Select(g => (uint)g.appid).ToHashSet();
            foreach (var appId in (store.Account(id)?.SelectedAppIds ?? []).Take(GameSelection.SteamLimit).Where(appId => !returnedIds.Contains(appId)))
            {
                try
                {
                    async Task<SteamApps.AppOwnershipTicketCallback> Ownership() => await apps.GetAppOwnershipTicket(appId);
                    var ownership = await Ownership().WaitAsync(TimeSpan.FromSeconds(8), stop.Token);
                    if (ownership.Result == EResult.OK && ownership.Ticket.Length > 0) verifiedMissing.Add(appId);
                    else events.Add("warning", id, $"Selected App {appId} was absent from the library and ownership could not be verified.");
                }
                catch (Exception error) when (error is TimeoutException or AsyncJobFailedException) { events.Add("warning", id, $"Ownership check for App {appId} did not complete. It remains inactive."); }
            }
            lock (gate)
            {
                var library = new Dictionary<uint, LibraryGame>();
                foreach (var game in result.Body.games.Where(x => x.appid > 0))
                {
                    var appId = (uint)game.appid;
                    var record = statistics.Games.GetValueOrDefault(appId) ?? new LibraryGame { AppId = appId };
                    record.Name = game.name ?? $"App {appId}"; record.SteamMinutes = (uint)Math.Max(0, game.playtime_forever); record.SteamHoursReported = true;
                    record.EstimatedTotalSeconds = Math.Max(record.EstimatedTotalSeconds, record.SteamMinutes * 60d); library[appId] = record;
                }
                foreach (var appId in verifiedMissing)
                {
                    var record = statistics.Games.GetValueOrDefault(appId) ?? new LibraryGame { AppId = appId, Name = appId == 570 ? "Dota 2" : $"App {appId}" };
                    record.SteamHoursReported = false;
                    library[appId] = record;
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
        var directInvitesEnabled = config.Enabled && GroupAutoJoin.Allows(config.AutoJoinGroups, JoinSource.Direct);
        if (!directInvitesEnabled) nextPendingClanSweep = DateTimeOffset.MinValue;
        else if (friendsReady && client.SteamID is not null && now >= nextPendingClanSweep)
        {
            nextPendingClanSweep = now.AddMinutes(10);
            QueuePendingClanInvites();
        }
        if (client.SteamID is not null && now >= nextGroupSync)
        { nextGroupSync = now.AddSeconds(30); _ = SyncGroupChatsAsync(); }
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
        if (config.Enabled && client.SteamID is { } steamId && now >= nextProfileCommentCheck)
        {
            nextProfileCommentCheck = now.AddSeconds(30);
            _ = CheckProfileCommentsAsync(steamId.ConvertToUInt64(), config.AiReplies.DailyLimit);
        }
        AcceptIncomingFriendRequests(config, now);
        ApplyPresence(config);
        var allowed = config.Enabled && !config.Paused && ScheduleRule.Allows(config.Schedule, now) && !view.PlayingElsewhere;
        lock (gate)
        {
            var rotate = config.SelectionMode == "rotate" && now - lastRotation >= TimeSpan.FromMinutes(config.RotationMinutes);
            chosen = GameSelection.Choose(config, statistics.Games, chosen, rotate);
            if (rotate) lastRotation = now;
        }
        ProfileDisplayPlan plan;
        lock (gate) plan = ProfileDisplayRule.Choose(config, statistics.Games, chosen, now, allowed);
        var playing = plan.SteamGames;
        lock (gate) { view.RequestedProfileDisplay = plan.Label; view.NextProfileDisplayChange = plan.NextChange; }
        var state = !config.Enabled || config.Paused ? "paused" : !ScheduleRule.Allows(config.Schedule, now) ? "scheduled" : view.PlayingElsewhere ? "playing_elsewhere" : playing.Count == 0 && plan.CustomText is null ? "idle" : "idling";
        Change(state, state switch
        {
            "paused" => "Paused by you.", "scheduled" => "Outside the configured schedule.",
            "playing_elsewhere" => "Paused while this account is playing elsewhere.",
            "idle" => "Choose owned games, or review exclusions and hour targets.",
            _ => $"Reporting {playing.Count} game(s) as playing."
        });
        if (!playing.SequenceEqual(view.PlayingAppIds) || plan.CustomText != lastCustomDisplay || now >= nextReassert)
        {
            if (!view.PlayingElsewhere)
            {
                // A clean transition helps Steam refresh non-Steam custom names.
                if (plan.CustomText is not null && plan.CustomText != lastCustomDisplay)
                {
                    client.Send(ProfileDisplayRule.Packet(new([], null, null, null)));
                    await Task.Delay(250, stop.Token);
                    if (!client.IsConnected || store.Account(id) is not { Enabled: true, Paused: false } || view.PlayingElsewhere) return;
                }
                client.Send(ProfileDisplayRule.Packet(plan));
            }
            lastCustomDisplay = plan.CustomText;
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
    public object ReadProfileComments()
    {
        var snapshot = Snapshot();
        return profileComments.Read(ProfileCommentOwner(), DateTimeOffset.UtcNow,
            snapshot.Connected && snapshot.LoggedIn ? profileCommentStatus : "Waiting for Steam");
    }

    private string ProfileCommentOwner() => Snapshot().SteamId
        ?? throw new ArgumentException("Connect this Steam account to use the profile comment inbox.");

    public void ConfigureProfileComments(bool enabled) =>
        profileComments.Configure(ProfileCommentOwner(), enabled, DateTimeOffset.UtcNow);

    public void DismissProfileComment(string id) => profileComments.Dismiss(ProfileCommentOwner(), id);

    private async Task CheckProfileCommentsAsync(ulong steamId, int dailyLimit)
    {
        if (!await profileCommentPoll.WaitAsync(0)) return;
        try
        {
            var owner = steamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!profileComments.Enabled(owner)) return;
            var token = stop.Token;
            var request = new CCommunity_GetCommentThread_Request
            {
                steamid = steamId,
                comment_thread_type = (int)SteamKit2.Internal.ECommentThreadType.k_ECommentThreadTypeProfile,
                count = 50,
                oldest_first = false
            };
            async Task<SteamUnifiedMessages.ServiceMethodResponse<CCommunity_GetCommentThread_Response>> Read()
                => await community.GetCommentThread(request);
            var result = await Read().WaitAsync(TimeSpan.FromSeconds(12), token);
            if (result.Result != EResult.OK)
            {
                profileCommentStatus = $"Steam returned {result.Result}";
                return;
            }
            var comments = result.Body.comments
                .Where(row => row.gidcomment != 0 && row.steamid != 0 && row.steamid != steamId
                    && new SteamID(row.steamid).AccountType == EAccountType.Individual
                    && !row.hidden && !row.hidden_by_user && !row.deleted && !string.IsNullOrWhiteSpace(row.text))
                .Select(row => new ProfileCommentInput(row.gidcomment.ToString(), row.steamid.ToString(),
                    DateTimeOffset.FromUnixTimeSeconds(row.timestamp), row.text));
            await profileComments.ObserveAsync(owner, comments, DateTimeOffset.UtcNow,
                (text, cancellation) => AiReplyService.For(store).ScreenCommentAsync(id, dailyLimit, text, cancellation), token);
            profileCommentStatus = "Steam profile checked";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { profileCommentStatus = "Profile comment check failed. HourBoostr will try again."; }
        finally { profileCommentPoll.Release(); }
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
    private void MarkSteamActivity()
    {
        lock (gate) { if (view.LoggedIn) { view.LastSteamResponse = DateTimeOffset.UtcNow; view.ConsecutiveProbeFailures = 0; } }
    }
    private async Task ProbeAsync()
    {
        if (!await healthCheck.WaitAsync(0, stop.Token)) return;
        try
        {
            var request = new CPlayer_GetOwnedGames_Request { steamid = client.SteamID!.ConvertToUInt64(), include_appinfo = false, include_played_free_games = true };
            lock (gate) { if (statistics.Games.Count > 0) request.appids_filter.Add(statistics.Games.Keys.First()); }
            async Task<SteamUnifiedMessages.ServiceMethodResponse<CPlayer_GetOwnedGames_Response>> Query() => await player.GetOwnedGames(request);
            var result = await Query().WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
            if (result.Result == EResult.OK) MarkSteamActivity();
            else { lock (gate) view.ConsecutiveProbeFailures++; events.Add("warning", id, "Steam health check returned " + result.Result); }
        }
        catch (Exception error) when (!stop.IsCancellationRequested) { lock (gate) view.ConsecutiveProbeFailures++; events.Add("warning", id, "Steam health check failed: " + error.GetType().Name); }
        finally { healthCheck.Release(); }
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
    private async Task SyncGroupChatsAsync()
    {
        if (!await groupSync.WaitAsync(0)) return;
        try
        {
            var config = store.Account(id);
            var owner = Snapshot().SteamId;
            if (owner is null || !client.IsConnected) return;
            var selected = config is { Enabled: true } ? groupRegulars.SelectedGroups(owner, DateTimeOffset.UtcNow) : [];
            var replyEnabled = config is { Enabled: true, GroupReplies.Enabled: true };
            var joinFromGroup = config is { Enabled: true } && GroupAutoJoin.Allows(config.AutoJoinGroups, JoinSource.GroupChat);
            var enabled = replyEnabled || joinFromGroup || selected.Length > 0;
            if (!enabled && !groupsActive) return;
            var request = new CChatRoom_SetSessionActiveChatRoomGroups_Request();
            if (enabled)
            {
                var joined = await GetRegularGroupsAsync(stop.Token);
                request.chat_group_ids.AddRange(joined.Where(group => replyEnabled || joinFromGroup || selected.Contains(group.Id))
                    .Select(group => ulong.Parse(group.Id, System.Globalization.CultureInfo.InvariantCulture)));
            }
            var active = await groupChat.SetSessionActiveChatRoomGroups(request);
            if (active.Result != EResult.OK) throw new InvalidOperationException();
            groupsActive = enabled; MarkSteamActivity();
            if (lastGroupCount != request.chat_group_ids.Count)
            {
                lastGroupCount = request.chat_group_ids.Count;
                events.Add("info", id, $"Subscribed to {lastGroupCount} joined Steam chat group(s).");
            }
        }
        catch (Exception) { if (!stop.IsCancellationRequested) events.Add("warning", id, "Group chat subscription could not be updated; will retry."); }
        finally { groupSync.Release(); }
    }
    private async Task<RegularGroup[]> GetRegularGroupsAsync(CancellationToken token)
    {
        async Task<SteamUnifiedMessages.ServiceMethodResponse<CChatRoom_GetMyChatRoomGroups_Response>> Query()
            => await groupChat.GetMyChatRoomGroups(new CChatRoom_GetMyChatRoomGroups_Request());
        var result = await Query().WaitAsync(TimeSpan.FromSeconds(12), token);
        if (result.Result != EResult.OK) throw new HttpRequestException("Steam did not return the joined groups.");
        return regularGroups = result.Body.chat_room_groups.Where(group => group.group_summary is not null)
            .Select(group => new RegularGroup(group.group_summary.chat_group_id.ToString(), group.group_summary.chat_group_name ?? "Steam group"))
            .DistinctBy(group => group.Id).OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string RegularsOwner()
    {
        var snapshot = Snapshot();
        if (!snapshot.LoggedIn || !client.IsConnected || snapshot.SteamId is null || store.Account(id) is not { Enabled: true })
            throw new ArgumentException("Connect this Steam account to use Group Regulars.");
        return snapshot.SteamId;
    }

    private bool CanSuggestFriend(ulong steamId)
    {
        var target = new SteamID(steamId);
        return friendsReady && target.IsValid && target.AccountType == EAccountType.Individual
            && !localAccounts.ContainsKey(steamId) && friends.GetFriendRelationship(target) == EFriendRelationship.None;
    }

    public async Task<object> ReadRegularsAsync(CancellationToken token)
    {
        var owner = RegularsOwner();
        var groups = await GetRegularGroupsAsync(token);
        if (RegularsOwner() != owner) throw new ArgumentException("The Steam account changed. Refresh Group Regulars.");
        var joined = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        var suggestions = groupRegulars.Suggestions(owner, joined, DateTimeOffset.UtcNow)
            .Where(person => CanSuggestFriend(ulong.Parse(person.SteamId))).Take(15);
        return new { groups, groupIds = groupRegulars.SelectedGroups(owner), suggestions = suggestions.ToArray(), consentCommand = $"!addme {owner}" };
    }

    public void ConfigureRegulars(RegularSettings request)
    {
        var owner = RegularsOwner();
        if (request.GroupIds is null || request.GroupIds.Length > 5 || request.GroupIds.Distinct().Count() != request.GroupIds.Length
            || request.GroupIds.Any(id => !regularGroups.Any(group => group.Id == id)))
            throw new ArgumentException("Choose up to five joined groups from the list.");
        groupRegulars.SaveGroups(owner, request.GroupIds);
        nextGroupSync = DateTimeOffset.MinValue;
    }

    public void ActOnRegular(RegularAction action)
    {
        var owner = RegularsOwner();
        if (action.Action is not ("add" or "dismiss") || !ulong.TryParse(action.SteamId, out var sender) || !CanSuggestFriend(sender))
            throw new ArgumentException("This person is no longer eligible for a friend request.");
        var joined = regularGroups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        groupRegulars.Act(owner, sender, joined, DateTimeOffset.UtcNow, action.Action == "add", () =>
        {
            if (RegularsOwner() != owner || !CanSuggestFriend(sender))
                throw new ArgumentException("The Steam connection or relationship changed.");
            friends.AddFriend(new SteamID(sender));
        });
    }

    private async Task ReplyToGroupAsync(CChatRoom_IncomingChatMessage_Notification incoming)
    {
        MarkSteamActivity();
        try
        {
            var config = store.Account(id);
            if (config?.GroupReplies.Enabled == true && DateTimeOffset.UtcNow >= nextGroupDiagnostic)
            {
                nextGroupDiagnostic = DateTimeOffset.UtcNow.AddSeconds(10);
                events.Add("info", id, $"Group notification received: system={incoming.server_message is not null}, text={!string.IsNullOrWhiteSpace(incoming.message)}, automatedSender={IsAutomatedGroupSender(incoming.steamid_sender)}, ageSeconds={DateTimeOffset.UtcNow.ToUnixTimeSeconds() - incoming.timestamp}.");
            }
            if (stop.IsCancellationRequested || config is not { Enabled: true } || client.SteamID is null || incoming.server_message is not null || string.IsNullOrWhiteSpace(incoming.message) || IsAutomatedGroupSender(incoming.steamid_sender)) return;
            var now = DateTimeOffset.UtcNow;
            if (incoming.timestamp >= now.ToUnixTimeSeconds() - 120
                && autoJoin.Observe(client.SteamID.ConvertToUInt64().ToString(), JoinSource.GroupChat,
                    $"{incoming.chat_group_id}:{incoming.chat_id}", incoming.message, config.AutoJoinGroups)) return;
            if (CanSuggestFriend(incoming.steamid_sender))
            {
                try
                {
                    var groupId = incoming.chat_group_id.ToString();
                    if (regularGroups.Any(group => group.Id == groupId)
                        && groupRegulars.Observe(client.SteamID.ConvertToUInt64().ToString(), incoming.steamid_sender,
                            friends.GetFriendPersonaName(new SteamID(incoming.steamid_sender)) ?? "", groupId, incoming.message,
                            DateTimeOffset.FromUnixTimeSeconds(incoming.timestamp), DateTimeOffset.UtcNow))
                        return;
                }
                catch (IOException)
                {
                    // A storage issue in the private suggestions feature must not stop existing chat replies.
                }
            }
            var room = $"{incoming.chat_group_id}:{incoming.chat_id}";
            var useAi = config.AiReplies.Groups;
            var reply = groupReplyGate.Reserve(config.GroupReplies, incoming.chat_group_id, incoming.chat_id, incoming.steamid_sender,
                client.SteamID.ConvertToUInt64(), incoming.timestamp, incoming.ordinal, DateTimeOffset.UtcNow, useAi);
            if (reply is null) return;
            var replySource = useAi ? "AI" : "preset";
            if (useAi)
            {
                reply = await AiReplyService.For(store).GenerateAsync(id, config.AiReplies, $"group:{incoming.chat_group_id}:{incoming.chat_id}",
                    incoming.message, stop.Token, groupSender: incoming.steamid_sender, waitForCapacity: true, preset: reason =>
                    {
                        if (!groupPresetGuard.Claim(room, DateTimeOffset.UtcNow)) return null;
                        replySource = $"preset, {reason}";
                        return groupReplyGate.NextPreset(config.GroupReplies, incoming.chat_group_id, incoming.chat_id);
                    });
                if (string.IsNullOrWhiteSpace(reply)) return;
            }
            await AutomaticReplyDelay.SendAsync(
                () => client.IsConnected && store.Account(id) is { Enabled: true, GroupReplies.Enabled: true } current && current.AiReplies.Groups == useAi,
                async () =>
                {
                    var sender = new SteamID(incoming.steamid_sender);
                    var addressed = MentionGroupSender(sender.AccountID, friends.GetFriendPersonaName(sender), reply);
                    var result = await groupChat.SendChatMessage(new CChatRoom_SendChatMessage_Request {
                        chat_group_id = incoming.chat_group_id, chat_id = incoming.chat_id, message = addressed, echo_to_sender = false });
                    var outcome = result.Result;
                    if (outcome == EResult.OK && replySource == "AI") groupPresetGuard.ConfirmAi(room, now);
                    events.Add(outcome == EResult.OK ? "info" : "warning", id, outcome == EResult.OK
                        ? $"Sent an automatic group reply ({replySource})."
                        : $"Steam returned {outcome} ({(int)outcome}) for an automatic group reply ({replySource}). No retry scheduled.");
                }, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!stop.IsCancellationRequested)
                events.Add("warning", id, $"Group reply could not be delivered ({error.GetType().Name}). No retry scheduled.");
        }
    }
    internal static string MentionGroupSender(uint accountId, string? personaName, string reply)
    {
        var label = new string((personaName ?? "").Where(c => !char.IsControl(c) && c is not ('[' or ']' or '\\')).ToArray()).Trim();
        if (label.Length == 0 || label == "[unknown]" || label == "unknown") label = "member";
        return FormattableString.Invariant($"[mention={accountId}]@{label}[/mention] {reply}");
    }

    private bool IsAutomatedGroupSender(ulong sender) => localAccounts.TryGetValue(sender, out var senderAccount)
        && store.Account(senderAccount) is { Enabled: true, GroupReplies.Enabled: true };
    private async Task SendFriendWelcomeAsync(ulong recipient)
    {
        try
        {
            var config = store.Account(id);
            if (stop.IsCancellationRequested || config is null || !friendWelcomeGate.TryClaim(recipient, true, config)) return;
            await AutomaticReplyDelay.SendAsync(
                () => client.IsConnected && store.Account(id) is { Enabled: true, AutoAcceptFriendRequests: true, FriendWelcomeEnabled: true },
                async () =>
                {
                    var result = await messages.SendMessage(new CFriendMessages_SendMessage_Request { steamid = recipient, chat_entry_type = (int)EChatEntryType.ChatMsg, message = config.FriendWelcomeMessage, contains_bbcode = false, echo_to_sender = false });
                    events.Add(result.Result == EResult.OK ? "info" : "warning", id, result.Result == EResult.OK ? "Sent the configured friend welcome message." : "Friend welcome delivery was not confirmed by Steam; no automatic retry will be made.");
                }, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) { if (!stop.IsCancellationRequested) events.Add("warning", id, "Friend welcome could not be delivered; no automatic retry will be made."); }
    }
    private async Task ReplyAsync(ulong sender, int entryType, bool localEcho, string text, string? plain = null, bool unified = false)
    {
        MarkSteamActivity();
        if (entryType != (int)EChatEntryType.ChatMsg || localEcho || stop.IsCancellationRequested || client.SteamID is not { } steamId) return;
        var self = steamId.ConvertToUInt64();
        if (sender == self || store.Account(id) is not { Enabled: true } config) return;
        var content = string.IsNullOrWhiteSpace(plain) ? text : plain;
        var now = DateTimeOffset.UtcNow;
        if (automaticReplies.Matches(sender, self, text, now) || automaticReplies.Matches(sender, self, content, now)
            || autoJoin.Observe(self.ToString(), JoinSource.PrivateChat, sender.ToString(), string.IsNullOrWhiteSpace(text) ? content : text, config.AutoJoinGroups))
            return;
        if (ReplySettings(sender, self) is null) return;
        var token = stop.Token;
        if (config.AiReplies.PrivateMessages)
            await aiReplyQueue.OfferAsync(sender, content, unified, (batch, cancellation) => SendPrivateReplyAsync(sender, self, batch, useAi: true, cancellation), token);
        else
            await SendPrivateReplyAsync(sender, self, content, useAi: false, token);
    }

    private AccountSettings? ReplySettings(ulong sender, ulong self)
    {
        var current = store.Account(id);
        return client.IsConnected && client.SteamID?.ConvertToUInt64() == self
            && current is { Enabled: true, Afk.Enabled: true }
            && friends.GetFriendRelationship(new SteamID(sender)) == EFriendRelationship.Friend
            && !current.Afk.ExcludedFriends.Contains(sender.ToString())
            && !(current.Afk.QuietHours.Enabled && ScheduleRule.Allows(current.Afk.QuietHours, DateTimeOffset.UtcNow)) ? current : null;
    }

    private async Task SendPrivateReplyAsync(ulong sender, ulong self, string text, bool useAi, CancellationToken token)
    {
        var replyGate = useAi ? aiReplyGate : presetReplyGate;
        var reserved = false;
        var delivered = false;
        try
        {
            if (ReplySettings(sender, self) is not { } config || config.AiReplies.PrivateMessages != useAi) return;
            if (useAi)
            {
                var remaining = replyGate.Remaining(sender, TimeSpan.FromSeconds(config.AiReplies.CooldownSeconds), DateTimeOffset.UtcNow);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
                if (ReplySettings(sender, self) is not { AiReplies.PrivateMessages: true } current) return;
                config = current;
            }
            reserved = replyGate.TryReserve(sender, self, true, false, config.Afk, DateTimeOffset.UtcNow, useAi ? config.AiReplies.CooldownSeconds : null);
            if (!reserved) return;
            var replySource = useAi ? "AI" : "preset";
            var reply = useAi ? await AiReplyService.For(store).GenerateAsync(id, config.AiReplies, "friend:" + sender, text, token,
                waitForCapacity: true, preset: reason => { replySource = $"preset, {reason}"; return config.Afk.Message; }) : config.Afk.Message;
            if (string.IsNullOrWhiteSpace(reply)) return;
            await AutomaticReplyDelay.SendAsync(
                () => ReplySettings(sender, self)?.AiReplies.PrivateMessages == useAi,
                async () =>
                {
                    automaticReplies.Record(self, sender, reply, DateTimeOffset.UtcNow);
                    var result = await messages.SendMessage(new CFriendMessages_SendMessage_Request { steamid = sender, chat_entry_type = (int)EChatEntryType.ChatMsg, message = reply, contains_bbcode = false, echo_to_sender = false });
                    var outcome = result.Result;
                    if (outcome != EResult.OK)
                    {
                        events.Add("warning", id, $"Steam returned {outcome} ({(int)outcome}) for an automatic private reply ({replySource}).");
                        return;
                    }
                    delivered = true;
                    events.Add("info", id, $"Sent an automatic private reply ({replySource}).");
                }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!token.IsCancellationRequested) events.Add("warning", id, "AFK reply could not be delivered."); }
        finally
        {
            if (reserved) replyGate.Complete(sender, delivered);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; password = null; stop.Cancel();
        client.Disconnect();
        if (callbackPump is not null) await callbackPump.WaitAsync(TimeSpan.FromSeconds(3));
        lock (gate) store.Write($"statistics-{id}.json", statistics);
        await autoJoin.StopAsync();
        stop.Dispose();
    }
}
