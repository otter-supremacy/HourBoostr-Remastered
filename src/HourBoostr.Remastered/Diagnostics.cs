using System.Runtime.InteropServices;

namespace HourBoostr.Remastered;

public static class Diagnostics
{
    public static object Report(DataStore store, SteamEngine? engine = null)
    {
        var views = engine?.Snapshot() ?? [];
        var writable = false;
        try { var path = store.Resolve(".doctor-probe"); File.WriteAllText(path, "probe"); File.Delete(path); writable = true; } catch (IOException) { }
        return new
        {
            product = "HourBoostr Remastered by Otter", version = ReleaseTools.CurrentVersion,
            generatedUtc = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription,
            operatingSystem = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            dataDirectoryWritable = writable,
            sessionProtection = OperatingSystem.IsWindows() ? "Windows DPAPI, current user" : "Owner-only session files (mode 600); no at-rest encryption",
            accountCount = store.Settings.Accounts.Count,
            accounts = views.Select((x, i) => new { account = i + 1, x.State, x.Connected, x.LoggedIn, gameCount = x.PlayingAppIds.Count, x.LastSteamResponse, x.Reconnects }),
            notes = new[] { "This report excludes login names, Steam IDs, passwords, session tokens, QR challenges and chat content.", "Game time shown by the app is estimated; Steam-reported minutes are separate.", "Free hosting availability and Steam account access are not guaranteed." }
        };
    }
}
