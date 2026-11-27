using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamKit2.WebUI.Internal;

namespace HourBoostr.Members;

internal sealed record SteamSecret(string Login, string Token);
internal sealed record LinkResult(string SteamId, SteamSecret Secret, Game[] Games);

internal sealed class MemberWorker(MemberStore store, IDataProtectionProvider protection, bool live, int capacity, MemberChatAi chatAi) : IAsyncDisposable
{
    private sealed class Run(string ticket)
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly string Ticket = ticket;
        public Task Task = Task.CompletedTask;
        public volatile string? Challenge;
        public volatile MemberChatSession? Chat;
    }

    private readonly IDataProtector vault = protection.CreateProtector("HourBoostr.Members.Steam.v1");
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Run> pairing = [];
    private readonly Dictionary<string, Run> workers = [];

    public async Task<bool> Link(string id)
    {
        await gate.WaitAsync();
        try
        {
            await StopRun(pairing, id);
            // Bound the number of QR challenges to avoid exhausting the small host.
            if (pairing.Values.Count(x => !x.Task.IsCompleted) >= capacity) return false;
            var run = new Run(Guid.NewGuid().ToString("N"));
            if (!store.BeginLink(id, run.Ticket)) { run.Cancellation.Dispose(); return false; }
            pairing[id] = run;
            run.Cancellation.CancelAfter(TimeSpan.FromMinutes(3));
            run.Task = CompletePairing(id, run);
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task CompletePairing(string id, Run run)
    {
        try
        {
            if (!live)
            {
                await Task.Delay(TimeSpan.FromSeconds(8), run.Cancellation.Token);
                if (store.CompleteLink(id, run.Ticket, "demo-" + id, null,
                    [new(10, "Counter-Strike"), new(570, "Dota 2"), new(730, "Counter-Strike 2")])) return;
            }
            else
            {
                var account = await SteamAdapter.Pair(url => run.Challenge = url, run.Cancellation.Token);
                if (store.CompleteLink(id, run.Ticket, account.SteamId, vault.Protect(JsonSerializer.Serialize(account.Secret)), account.Games)) return;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException
            or SteamKit2.Authentication.AuthenticationException or InvalidOperationException or CryptographicException) { }
        finally
        {
            run.Challenge = null;
            store.FailLink(id, run.Ticket);
        }
    }

    public async Task<byte[]?> Qr(string id)
    {
        await gate.WaitAsync();
        try
        {
            if (store.Get(id)?.State != "pairing" || !pairing.TryGetValue(id, out var pending) || pending.Challenge is not { } challenge) return null;
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(challenge, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            return qr.GetGraphic(5);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> Start(string id)
    {
        await gate.WaitAsync();
        try
        {
            if (workers.TryGetValue(id, out var existing) && !existing.Task.IsCompleted) return false;
            await StopRun(workers, id);
            if (store.Start(id, capacity) is not { } work) return false;
            var run = new Run(work.Ticket);
            workers[id] = run;
            run.Task = RunWorker(work, run);
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task RunWorker(Work work, Run run)
    {
        var stop = run.Cancellation.Token;
        var end = "paused";
        try
        {
            var failure = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    MemberChatSession? chat = null;
                    await using var steam = live
                        ? await SteamAdapter.Connect(JsonSerializer.Deserialize<SteamSecret>(vault.Unprotect(work.ProtectedSession!))!, stop,
                            (client, callbacks) => chat = new MemberChatSession(client, callbacks, store, chatAi, work, stop))
                        : null;
                    if (steam is not null && !steam.CanPlay) throw new IOException();
                    if (!store.WorkerState(work, "idling")) return;
                    var appearance = steam is null ? null : store.Appearance(work.Id);
                    if (steam is not null)
                    {
                        if (appearance is not null) steam.SetAppearance(appearance);
                        steam.Play(work.Game!.Value, appearance?.CustomText);
                    }
                    run.Chat = chat;
                    chat?.Tick();
                    var lastTick = Stopwatch.GetTimestamp();
                    using var clock = new PeriodicTimer(TimeSpan.FromSeconds(5));
                    while (await clock.WaitForNextTickAsync(stop))
                    {
                        if (steam is not null && !steam.CanPlay) throw new IOException();
                        if (!store.WorkerState(work, "idling", Math.Clamp((int)Stopwatch.GetElapsedTime(lastTick).TotalSeconds, 0, 5))) return;
                        if (steam is not null && store.Appearance(work.Id) is { } next && next != appearance)
                        {
                            if (next.Status != appearance?.Status || next.Badge != appearance?.Badge) steam.SetAppearance(next);
                            if (next.CustomText != (appearance?.CustomText ?? "")) steam.Play(work.Game!.Value, next.CustomText);
                            appearance = next;
                        }
                        chat?.Tick();
                        lastTick = Stopwatch.GetTimestamp();
                        failure = 0;
                    }
                }
                catch (Exception error) when (error is IOException or TimeoutException)
                {
                    run.Chat = null;
                    if (++failure > 5 || !store.WorkerState(work, "reconnecting")) { end = "connection_failed"; break; }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 15 * (1 << failure))), stop);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) { end = "connection_failed"; }
        finally { run.Chat = null; store.WorkerEnded(work, end); }
    }

    public async Task<bool> SetGame(string id, uint appId)
    {
        await gate.WaitAsync();
        try
        {
            if (!store.Library(id).Any(game => game.AppId == appId)) return false;
            var resume = store.Get(id)?.Requested == true;
            store.Stop(id, false);
            await StopRun(workers, id);
            if (!store.Select(id, appId)) return false;
            if (resume && store.Start(id, capacity) is { } work)
            {
                var run = new Run(work.Ticket);
                workers[id] = run;
                run.Task = RunWorker(work, run);
            }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task Stop(string id, bool unlink)
    {
        await gate.WaitAsync();
        try
        {
            store.Stop(id, unlink);
            if (unlink) await StopRun(pairing, id);
            await StopRun(workers, id);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> Moderate(string id, bool banned, string device)
    {
        await gate.WaitAsync();
        try
        {
            if (!store.Moderate(id, banned, device)) return false;
            if (banned)
            {
                await StopRun(pairing, id);
                await StopRun(workers, id);
                store.ConfirmBanned(id);
            }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<object> Regulars(string id)
    {
        await gate.WaitAsync();
        try
        {
            var chat = workers.TryGetValue(id, out var run) ? run.Chat : null;
            ChatGroup[] groups = live ? chat?.Groups ?? [] : [new("10001", "Demo Steam group")];
            var candidates = store.Regulars(id).Where(x => groups.Any(group => group.Id == x.GroupId)).ToArray();
            return new { groups, candidates, optIn = chat?.OptIn, connected = chat is not null };
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ApproveRegular(string id, ulong sender)
    {
        await gate.WaitAsync();
        try { return live && workers.TryGetValue(id, out var run) && run.Chat?.ApproveRegular(sender) == true; }
        finally { gate.Release(); }
    }

    private static async Task StopRun(Dictionary<string, Run> runs, string id)
    {
        if (!runs.Remove(id, out var run)) return;
        using var cancellation = run.Cancellation;
        await cancellation.CancelAsync();
        await run.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            foreach (var id in workers.Keys.ToArray()) await StopRun(workers, id);
            foreach (var id in pairing.Keys.ToArray()) await StopRun(pairing, id);
        }
        finally { gate.Release(); }
    }
}

internal sealed class SteamAdapter : IAsyncDisposable
{
    private readonly SteamClient client = new(SteamConfiguration.Create(options => options
        .WithProtocolTypes(ProtocolTypes.WebSocket | ProtocolTypes.Tcp)
        .WithConnectionTimeout(TimeSpan.FromSeconds(15))));
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SteamUser.LoggedOnCallback> logon = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource stop = new();
    private readonly Task pump;
    private volatile bool blocked;

    private SteamAdapter(Action<SteamClient, CallbackManager>? configure = null)
    {
        var callbacks = new CallbackManager(client);
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => ready.TrySetResult());
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(result => logon.TrySetResult(result));
        callbacks.Subscribe<SteamUser.PlayingSessionStateCallback>(state => blocked = state.PlayingBlocked);
        callbacks.Subscribe<SteamUser.LoggedOffCallback>(_ => blocked = true);
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            blocked = true;
            ready.TrySetException(new IOException("Steam disconnected."));
            logon.TrySetException(new IOException("Steam disconnected."));
        });
        configure?.Invoke(client, callbacks);
        pump = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
        });
        client.Connect();
    }

    public bool CanPlay => client.IsConnected && !blocked && logon.Task.IsCompletedSuccessfully && logon.Task.Result.Result == EResult.OK;

    public static async Task<LinkResult> Pair(Action<string> showQr, CancellationToken token)
    {
        await using var steam = new SteamAdapter();
        await steam.ready.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        var session = await steam.client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
            DeviceFriendlyName = "HourBoostr Members", IsPersistentSession = true
        }).WaitAsync(TimeSpan.FromSeconds(20), token);
        session.ChallengeURLChanged = () => showQr(session.ChallengeURL);
        showQr(session.ChallengeURL);
        var poll = await session.PollingWaitForResultAsync(token);
        var secret = new SteamSecret(poll.AccountName, poll.RefreshToken);
        await steam.LogIn(secret, token);
        var steamId = steam.client.SteamID!.ConvertToUInt64();
        var player = steam.client.GetHandler<SteamUnifiedMessages>()!.CreateService<Player>();
        var response = await player.GetOwnedGames(new CPlayer_GetOwnedGames_Request
        {
            steamid = steamId, include_appinfo = true, include_played_free_games = true
        }).ToTask().WaitAsync(TimeSpan.FromSeconds(20), token);
        if (response.Result != EResult.OK) throw new IOException("Steam library request failed.");
        var games = response.Body.games.Where(x => x.appid > 0)
            .Select(x => new Game((uint)x.appid, string.IsNullOrWhiteSpace(x.name) ? $"App {x.appid}" : x.name))
            .DistinctBy(x => x.AppId).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(steamId.ToString(), secret, games);
    }

    public static async Task<SteamAdapter> Connect(SteamSecret secret, CancellationToken token,
        Action<SteamClient, CallbackManager> configure)
    {
        var steam = new SteamAdapter(configure);
        try { await steam.LogIn(secret, token); return steam; }
        catch { await steam.DisposeAsync(); throw; }
    }

    private async Task LogIn(SteamSecret secret, CancellationToken token)
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        client.GetHandler<SteamUser>()!.LogOn(new SteamUser.LogOnDetails
        {
            Username = secret.Login, AccessToken = secret.Token, ShouldRememberPassword = true,
            ChatMode = SteamUser.ChatMode.NewSteamChat,
            LoginID = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue)
        });
        var result = await logon.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        if (result.Result != EResult.OK) throw new IOException("Steam sign-in rejected.");
    }

    public void SetAppearance(MemberAppearance appearance) =>
        client.Send(HourBoostr.Remastered.SteamAppearance.Packet(appearance.Status, appearance.Badge));

    public void Play(uint? game, string? customText = null)
    {
        var packet = new ClientMsgProtobuf<SteamKit2.Internal.CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
        if (game is { } appId)
        {
            if (!string.IsNullOrEmpty(customText))
                packet.Body.games_played.Add(new() { game_id = new GameID { AppType = GameID.GameType.Shortcut, ModID = uint.MaxValue }, game_extra_info = customText });
            packet.Body.games_played.Add(new() { game_id = appId });
        }
        client.Send(packet);
    }

    public async ValueTask DisposeAsync()
    {
        if (client.IsConnected && logon.Task.IsCompletedSuccessfully && logon.Task.Result.Result == EResult.OK) Play(null);
        client.Disconnect();
        await stop.CancelAsync();
        await pump;
        stop.Dispose();
    }
}
