using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HourBoostr.Remastered;

public static class Cli
{
    public static async Task<int?> ExecuteAsync(string[] args, LaunchOptions options, DataStore store, SecretVault vault)
    {
        var command = args.FirstOrDefault() ?? "serve";
        if (command is "serve" or "--data-dir" or "--port" or "--listen") return null;
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                HourBoostr Remastered by Otter
                serve                 Run the local dashboard and Steam workers
                open                  Open the dashboard with the local pairing key
                dashboard-key         Print the local dashboard pairing key (keep private)
                status / accounts     List running account states
                stop                  Gracefully stop this running installation
                pause / resume ID     Pause or resume an account; use 'all' for every account
                login ID [--method password|qr]   Sign in through the running app
                doctor [--live]       Export redacted environment and connection diagnostics
                updates check         Check the configured remaster release repository
                updates stage --file ZIP --sha256 HASH   Verify and extract an update
                updates apply --staged DIR --install DIR Apply to a stopped managed release
                updates rollback --install DIR          Restore its previous release

                Options: --data-dir DIR --port 3241
                Remote HTTPS: --listen 0.0.0.0 --allow-remote --public-host HOST --cert FILE
                Certificate password: HBR_CERT_PASSWORD (environment variable)
                """);
            return 0;
        }
        var security = new DashboardSecurity(vault);
        if (command == "dashboard-key") { Console.WriteLine(security.Key); return 0; }
        if (command == "open")
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(options.BaseUrl + "/#key=" + security.Key) { UseShellExecute = true }); return 0;
        }
        if (command == "doctor" && !args.Contains("--live")) { Print(Diagnostics.Report(store)); return 0; }
        if (command == "updates")
        {
            var operation = args.ElementAtOrDefault(1) ?? "check";
            if (operation == "check") { Print(await ReleaseTools.CheckAsync(store.Settings.ReleaseRepository)); return 0; }
            if (operation == "stage") { Console.WriteLine(ReleaseTools.Stage(LaunchOptions.Option(args, "--file") ?? throw new ArgumentException("Supply --file."), LaunchOptions.Option(args, "--sha256") ?? "", Path.Combine(store.Root, "updates"))); return 0; }
            if (operation is "apply" or "rollback")
            {
                using var instanceLock = new FileStream(store.Resolve("instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (operation == "apply") ReleaseTools.Apply(LaunchOptions.Option(args, "--staged") ?? "", LaunchOptions.Option(args, "--install") ?? "");
                else ReleaseTools.Rollback(LaunchOptions.Option(args, "--install") ?? "");
                return 0;
            }
            throw new ArgumentException("Unknown update command.");
        }
        using var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", security.Key);
        http.DefaultRequestHeaders.Add("X-HBR-Request", "1");
        if (command is "status" or "accounts" or "doctor")
        { Console.WriteLine(await http.GetStringAsync(command == "doctor" ? "/api/diagnostics" : "/api/state")); return 0; }
        if (command == "stop") { using var response = await http.PostAsync("/api/shutdown", null); response.EnsureSuccessStatusCode(); Console.WriteLine("Shutdown requested."); return 0; }
        var id = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Supply an account ID, or 'all' for pause/resume.");
        if (command is "pause" or "resume")
        { using var response = await http.PostAsync($"/api/accounts/{Uri.EscapeDataString(id)}/{command}", null); response.EnsureSuccessStatusCode(); Console.WriteLine(command + " requested"); return 0; }
        if (command == "login")
        {
            var method = LaunchOptions.Option(args, "--method") ?? "password";
            string? name = null, password = null;
            if (method == "password") { Console.Write("Steam login name: "); name = Console.ReadLine(); Console.Write("Password: "); password = ReadPassword(); }
            using var content = new StringContent(JsonSerializer.Serialize(new LoginRequest(method, name, password), JsonDefaults.Options), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"/api/accounts/{id}/login", content); password = null;
            response.EnsureSuccessStatusCode();
            Console.WriteLine("Sign-in started. Open the dashboard for QR or Steam Guard prompts."); return 0;
        }
        throw new ArgumentException("Unknown command. Run with --help.");
    }
    public static string ReadPassword()
    {
        if (Console.IsInputRedirected) throw new ArgumentException("Enter passwords in an interactive terminal or the local dashboard.");
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) { value.Length--; Console.Write("\b \b"); } }
            else if (!char.IsControl(key.KeyChar)) { value.Append(key.KeyChar); Console.Write('*'); }
        }
    }
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
}
