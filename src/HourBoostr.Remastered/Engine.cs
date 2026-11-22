using System.Collections.Concurrent;

namespace HourBoostr.Remastered;

public sealed class SteamEngine(DataStore store, SecretVault vault, EventLog events, IEnumerable<string> protectedNames) : BackgroundService
{
    private readonly ConcurrentDictionary<string, SteamSession> sessions = new();
    private readonly SemaphoreSlim changes = new(1, 1);
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    private readonly string[] protectedAccounts = protectedNames.ToArray();
    public AccountView[] Snapshot()
    {
        var views = store.Settings.Accounts.Select(config => sessions.TryGetValue(config.Id, out var session) ? session.Snapshot() : new AccountView
            { Id = config.Id, Label = config.Label, State = "signed_out", HasSavedSession = vault.Get(config.Id) is not null }).ToArray();
        foreach (var view in views)
            if (ulong.TryParse(view.SteamId, out var steamId))
                view.FriendObservedGame = sessions.Where(x => x.Key != view.Id).Select(x => x.Value.ObserveFriendGame(steamId)).FirstOrDefault(x => x is not null);
        return views;
    }
    public AccountStatistics Statistics(string id) => sessions.TryGetValue(id, out var session) ? session.Statistics() : store.Read<AccountStatistics>($"statistics-{id}.json") ?? new();
    public async Task LoginAsync(string id, LoginRequest request)
    {
        if (request.Method is not ("password" or "qr")) throw new ArgumentException("Choose password or QR sign-in.");
        if (request.Method == "password" && (string.IsNullOrWhiteSpace(request.LoginName) || string.IsNullOrEmpty(request.Password))) throw new ArgumentException("Enter the account login name and password.");
        if (request.Password?.Length > 512 || request.LoginName?.Length > 100) throw new ArgumentException("Sign-in input is too long.");
        if (protectedAccounts.Contains(request.LoginName ?? "", StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("This account is protected from use in this installation.");
        if (request.Method == "password" && store.Settings.Accounts.Any(x => x.Id != id && x.LoginName.Length > 0 && string.Equals(x.LoginName, request.LoginName?.Trim(), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That Steam account is already configured. Use its existing profile.");
        await ReplaceAsync(id, request);
    }
    private async Task ReplaceAsync(string id, LoginRequest? request = null)
    {
        await changes.WaitAsync();
        try
        {
            var config = store.Account(id) ?? throw new KeyNotFoundException("Account not found.");
            if (sessions.TryRemove(id, out var old))
            {
                try { await old.DisposeAsync(); } catch (TimeoutException) { events.Add("warning", id, "Stopped a worker whose callback loop did not respond."); }
            }
            var protectedForSession = protectedAccounts.Concat(store.Settings.Accounts.Where(x => x.Id != id && x.LoginName.Length > 0).Select(x => x.LoginName));
            var session = new SteamSession(config, store, vault, events, protectedForSession, request);
            sessions[id] = session; session.Start();
        }
        finally { changes.Release(); }
    }
    public bool SubmitGuard(string id, string code) => sessions.TryGetValue(id, out var session) && session.SubmitGuard(code);
    public SteamSession RegularsSession(string id) => sessions.TryGetValue(id, out var session)
        ? session : throw new ArgumentException("Connect this Steam account to use Group Regulars.");
    public Task RefreshAsync(string id) => sessions.TryGetValue(id, out var session) ? session.RefreshLibraryAsync() : Task.CompletedTask;
    public void Reselect(string id) { if (sessions.TryGetValue(id, out var session)) session.Reselect(); }
    public async Task ForgetAsync(string id)
    {
        await changes.WaitAsync();
        try
        {
            if (sessions.TryRemove(id, out var session)) await session.DisposeAsync();
            vault.Forget(id); events.Add("info", id, "Forgot the local Steam session. This does not revoke other Steam devices.");
        }
        finally { changes.Release(); }
    }
    public async Task RemoveAsync(string id) { await ForgetAsync(id); store.Remove(id); }
    public void Pause(string id, bool paused)
    {
        foreach (var config in store.Settings.Accounts.Where(x => id == "all" || x.Id == id)) { config.Paused = paused; store.Save(config); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var config in store.Settings.Accounts.Where(x => x.Enabled))
            try { await ReplaceAsync(config.Id); } catch (Exception error) { events.Add("warning", config.Id, "Worker startup failed: " + error.GetType().Name); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var config in store.Settings.Accounts.Where(x => x.Enabled && !sessions.ContainsKey(x.Id)))
                    if (vault.Get(config.Id) is not null)
                        try { await ReplaceAsync(config.Id); } catch (Exception error) { events.Add("warning", config.Id, "Session restore failed: " + error.GetType().Name); }
                foreach (var pair in sessions.ToArray())
                {
                    try
                    {
                        var snapshot = pair.Value.Snapshot();
                        var now = DateTimeOffset.UtcNow;
                        if (snapshot.State != "auth_required" && vault.Get(pair.Key) is not null && (HealthRule.NeedsRecovery(snapshot, now) || now - pair.Value.LastPumpUtc > TimeSpan.FromMinutes(2)))
                        { events.Add("warning", pair.Key, "Watchdog is recovering a stalled Steam worker."); await ReplaceAsync(pair.Key); }
                        else await pair.Value.TickAsync(now);
                    }
                    catch (Exception error) { events.Add("warning", pair.Key, "Worker check failed: " + error.GetType().Name); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var session in sessions.Values) try { await session.DisposeAsync(); } catch (TimeoutException) { }
            sessions.Clear();
        }
    }
}
