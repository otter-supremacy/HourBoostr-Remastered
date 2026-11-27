using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HourBoostr.Remastered;

public sealed class DataStore
{
    public string Root { get; }
    private readonly object gate = new();
    private AppSettings settings;
    public DataStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        settings = Read<AppSettings>("settings.json") ?? new();
        if (settings.Accounts is null || settings.ProtectedLoginNames is null) throw new ArgumentException("The account configuration is incomplete.");
        if (settings.SchemaVersion != 1) throw new ArgumentException("Unsupported settings schema version. Restore a compatible backup or use a compatible release.");
        foreach (var account in settings.Accounts) account.Validate();
    }
    public static string DefaultPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HourBoostrRemastered");
    public AppSettings Settings { get { lock (gate) return JsonDefaults.Clone(settings); } }
    public AccountSettings? Account(string id) => Settings.Accounts.FirstOrDefault(x => x.Id == id);
    public AccountSettings Add(string label)
    {
        lock (gate)
        {
            var account = new AccountSettings { Label = label }; account.Validate();
            settings.Accounts.Add(account); Write("settings.json", settings); return JsonDefaults.Clone(account);
        }
    }
    public void Save(AccountSettings account)
    {
        account.Validate();
        lock (gate)
        {
            var index = settings.Accounts.FindIndex(x => x.Id == account.Id);
            if (index < 0) throw new KeyNotFoundException("Account not found.");
            settings.Accounts[index] = JsonDefaults.Clone(account); Write("settings.json", settings);
        }
    }
    public void Remove(string id)
    {
        lock (gate) { settings.Accounts.RemoveAll(x => x.Id == id); Write("settings.json", settings); }
        foreach (var name in new[] { $"statistics-{id}.json", $"session-{id}.secret", $"friend-welcomes-{id}.json", $"group-regulars-{id}.json", $"group-fallbacks-{id}.json", $"group-auto-join-{id}.json", $"profile-comments-{id}.json" })
        { var path = Resolve(name); if (File.Exists(path)) File.Delete(path); }
    }
    public void SetReleaseRepository(string? repository)
    { lock (gate) { settings.ReleaseRepository = repository; Write("settings.json", settings); } }
    public void ProtectAccounts(IEnumerable<string> names)
    { lock (gate) { settings.ProtectedLoginNames = settings.ProtectedLoginNames.Concat(names).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); Write("settings.json", settings); } }
    public T? Read<T>(string name)
    {
        lock (gate)
        {
            var path = Resolve(name);
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options);
        }
    }
    public void Write<T>(string name, T value)
    {
        lock (gate)
        {
            var path = Resolve(name); var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonDefaults.Options); stream.Flush(true);
            }
            Restrict(temporary); File.Move(temporary, path, true); Restrict(path);
        }
    }
    public string Resolve(string name)
    {
        if (name != Path.GetFileName(name)) throw new ArgumentException("Invalid data file name.");
        return Path.Combine(Root, name);
    }
    public static void Restrict(string path)
    { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
}

public sealed class SecretVault(DataStore store)
{
    private readonly object gate = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HourBoostr.Remastered.sessions.v1");
    public void Save(string id, SessionSecret secret)
    {
        lock (gate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(secret, JsonDefaults.Options);
            var encrypted = OperatingSystem.IsWindows() ? ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes;
            var path = store.Resolve($"session-{id}.secret"); var temp = path + ".tmp";
            File.WriteAllBytes(temp, encrypted); DataStore.Restrict(temp); File.Move(temp, path, true); DataStore.Restrict(path);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
    public SessionSecret? Get(string id)
    {
        lock (gate)
        {
            var path = store.Resolve($"session-{id}.secret"); if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            try
            {
                var raw = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes;
                try { return JsonSerializer.Deserialize<SessionSecret>(raw, JsonDefaults.Options); }
                finally { CryptographicOperations.ZeroMemory(raw); }
            }
            catch (Exception error) when (error is CryptographicException or JsonException) { return null; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    public void Forget(string id) { lock (gate) { var path = store.Resolve($"session-{id}.secret"); if (File.Exists(path)) File.Delete(path); } }
}

public sealed class EventLog(DataStore store)
{
    private readonly Queue<LogEntry> entries = new((store.Read<LogEntry[]>("events.json") ?? []).TakeLast(400));
    private readonly object gate = new();
    public void Add(string level, string? accountId, string message)
    {
        // Callers provide fixed messages/result codes, never authentication payloads or chat bodies.
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, accountId, message);
        lock (gate)
        {
            entries.Enqueue(entry); while (entries.Count > 400) entries.Dequeue();
            store.Write("events.json", entries.ToArray());
        }
    }
    public LogEntry[] Entries(string? id = null) { lock (gate) return entries.Where(x => id == null || x.AccountId == id).Reverse().ToArray(); }
}
