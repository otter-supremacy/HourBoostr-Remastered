using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace HourBoostr.Desktop;

internal static class Program
{
    internal const string Brand = "HourBoostr Remastered by Otter";
    internal const string SteamUrl = "https://steamcommunity.com/id/pornhub_sex5/";
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var options = DesktopOptions.Parse(args);
            using var mutex = new Mutex(true, @"Local\" + options.InstanceName, out var first);
            if (!first)
            {
                var command = args.Contains("--desktop-command") ? args.ElementAtOrDefault(Array.IndexOf(args, "--desktop-command") + 1) : options.StartMinimized ? "status" : "show";
                if (command is not ("status" or "show" or "minimize" or "exit" or "startup-on" or "startup-off")) throw new ArgumentException("Choose status, show, minimize, exit, startup-on or startup-off.");
                {
                    using var pipe = new NamedPipeClientStream(".", options.InstanceName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                    pipe.Connect(3000);
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true }; writer.WriteLine(command);
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    Console.WriteLine(reader.ReadLineAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).AsTask().GetAwaiter().GetResult());
                }
                return;
            }
            if (args.Contains("--desktop-command")) throw new ArgumentException("Start this desktop installation before sending a desktop command.");
            using var context = new DesktopContext(options);
            Application.Run(context);
            mutex.ReleaseMutex();
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException or ObjectDisposedException || error.HResult == unchecked((int)0x80004004)) return;
            if (args.Contains("--desktop-command")) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 1; }
            else MessageBox.Show(error.Message, Brand, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed record DesktopOptions(string DataDirectory, int Port, bool StartMinimized)
{
    internal string BaseUrl => $"http://127.0.0.1:{Port}";
    internal string InstanceName => "HBR-Otter-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..20];
    internal string Server => Path.Combine(AppContext.BaseDirectory, "HourBoostr.Remastered.exe");
    internal static DesktopOptions Parse(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 == args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException("Missing value for " + name);
            return args[index + 1];
        }
        var data = Path.GetFullPath(Option("--data-dir") ?? Environment.GetEnvironmentVariable("HBR_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HourBoostrRemastered"));
        if (!int.TryParse(Option("--port") ?? "3241", out var port) || port is < 1024 or > 65535) throw new ArgumentException("Port must be between 1024 and 65535.");
        return new(data, port, args.Contains("--start-minimized"));
    }
}

internal sealed class StartupRegistration(DesktopOptions options)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private string Name => "HourBoostr Remastered by Otter " + options.InstanceName;
    internal string Command => $"\"{Environment.ProcessPath}\" --data-dir \"{options.DataDirectory}\" --port {options.Port} --start-minimized";
    internal bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.Ordinal); }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (value) key.SetValue(Name, Command, RegistryValueKind.String);
            else key.DeleteValue(Name, false);
        }
    }
}

internal sealed class DesktopContext : ApplicationContext
{
    private readonly DesktopOptions options;
    private readonly StartupRegistration startup;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem closeItem;
    private readonly Form window;
    private readonly WebView2 web;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken stopToken;
    private readonly HttpClient http;
    private Process? server;
    private string? pairingKey;
    private bool exiting, ready, recovering, disposed, minimizeOnClose = true;
    private Task? webInitialization;
    private readonly string preferencesPath;
    private readonly System.Windows.Forms.Timer recovery = new() { Interval = 10000 };

    internal DesktopContext(DesktopOptions options)
    {
        stopToken = stop.Token;
        this.options = options; startup = new(options);
        Directory.CreateDirectory(options.DataDirectory);
        preferencesPath = Path.Combine(options.DataDirectory, "desktop.json");
        try { if (File.Exists(preferencesPath)) minimizeOnClose = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(preferencesPath))?.MinimizeOnClose ?? true; }
        catch (JsonException) { }
        http = new() { BaseAddress = new Uri(options.BaseUrl), Timeout = TimeSpan.FromSeconds(3) };
        window = new Form { Text = Program.Brand, Width = 1280, Height = 880, MinimumSize = new(800, 600), StartPosition = FormStartPosition.CenterScreen, Icon = SystemIcons.Application };
        var menu = new MenuStrip();
        var appMenu = new ToolStripMenuItem("App");
        startupItem = new("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = startup.Enabled };
        closeItem = new("Close window to tray", null, (_, _) => SetMinimizeOnClose(!minimizeOnClose)) { Checked = minimizeOnClose };
        appMenu.DropDownItems.Add("Minimize to tray", null, (_, _) => MinimizeToTray());
        appMenu.DropDownItems.Add(startupItem); appMenu.DropDownItems.Add(closeItem);
        appMenu.DropDownItems.Add(new ToolStripSeparator());
        appMenu.DropDownItems.Add("Exit", null, async (_, _) => await QuitAsync());
        menu.Items.Add(appMenu);
        menu.Items.Add("Otter’s Steam profile", null, (_, _) => OpenReference());
        window.MainMenuStrip = menu;
        web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(13, 18, 28) };
        window.Controls.Add(web); window.Controls.Add(menu);
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open " + Program.Brand, null, (_, _) => ShowWindow());
        trayMenu.Items.Add("Start with Windows", null, (_, _) => ToggleStartup());
        trayMenu.Opening += (_, _) => { ((ToolStripMenuItem)trayMenu.Items[1]).Checked = startup.Enabled; };
        trayMenu.Items.Add("Otter’s Steam profile", null, (_, _) => OpenReference());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, async (_, _) => await QuitAsync());
        tray = new NotifyIcon { Icon = window.Icon, Text = Program.Brand, ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();
        window.Resize += (_, _) => { if (window.WindowState == FormWindowState.Minimized) MinimizeToTray(); };
        window.FormClosing += async (_, e) =>
        {
            if (exiting || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) return;
            e.Cancel = true;
            if (minimizeOnClose) MinimizeToTray(); else await QuitAsync();
        };
        _ = window.Handle; // Create the dispatch target without displaying a startup window.
        _ = ListenAsync();
        recovery.Tick += async (_, _) =>
        {
            if (!recovering && server is { HasExited: true })
            {
                ready = false; recovering = true;
                try { await EnsureServerAsync(); ready = true; if (web.CoreWebView2 is not null) web.Reload(); }
                catch { tray.ShowBalloonTip(4000, Program.Brand, "The Steam worker could not restart. Open the app for details.", ToolTipIcon.Warning); }
                finally { recovering = false; }
            }
        };
        Application.Idle += InitializeOnce;
    }

    private async void InitializeOnce(object? sender, EventArgs e)
    {
        Application.Idle -= InitializeOnce;
        try
        {
            if (!options.StartMinimized) ShowWindow();
            await EnsureServerAsync();
            ready = true; recovery.Start();
            if (!options.StartMinimized) await InitializeWebAsync();
        }
        catch (Exception error)
        {
            if (IsExpectedCancellation(error)) return;
            if (!exiting && !window.IsDisposed) MessageBox.Show(window, error.Message, Program.Brand, MessageBoxButtons.OK, MessageBoxIcon.Error);
            await QuitAsync();
        }
    }
    private Process StartCore(params string[] arguments)
    {
        if (!File.Exists(options.Server)) throw new FileNotFoundException("Keep HourBoostr.Desktop.exe beside the published HourBoostr.Remastered.exe.");
        var info = new ProcessStartInfo(options.Server) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(options.DataDirectory);
        info.ArgumentList.Add("--port"); info.ArgumentList.Add(options.Port.ToString());
        return Process.Start(info) ?? throw new IOException("The Steam worker did not start.");
    }
    private async Task EnsureServerAsync()
    {
        if (pairingKey is null)
        {
            using var keyProcess = StartCore("dashboard-key");
            var output = keyProcess.StandardOutput.ReadToEndAsync(stopToken);
            var error = keyProcess.StandardError.ReadToEndAsync(stopToken);
            await keyProcess.WaitForExitAsync(stopToken);
            pairingKey = (await output).Trim(); await error;
            if (keyProcess.ExitCode != 0 || pairingKey.Length != 64) throw new IOException("Could not unlock the local dashboard.");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pairingKey);
            http.DefaultRequestHeaders.Add("X-HBR-Request", "1");
        }
        if (await IsReadyAsync()) return;
        server?.Dispose(); server = StartCore("serve");
        server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
        server.BeginOutputReadLine(); server.BeginErrorReadLine();
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (server.HasExited) throw new IOException("The Steam worker exited. Check that this data directory and dashboard port are not used by a different installation.");
            if (await IsReadyAsync()) return;
            await Task.Delay(500, stopToken);
        }
        throw new TimeoutException("The local dashboard did not become ready.");
    }
    private async Task<bool> IsReadyAsync()
    {
        try { using var response = await http.GetAsync("/api/state", stopToken); return response.IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!stop.IsCancellationRequested) { return false; }
    }
    private Task InitializeWebAsync() => webInitialization ??= InitializeWebCoreAsync();
    private async Task InitializeWebCoreAsync()
    {
        if (exiting || stop.IsCancellationRequested || web.IsDisposed || window.IsDisposed || web.CoreWebView2 is not null) return;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(options.DataDirectory, "webview"));
            if (exiting || stop.IsCancellationRequested || web.IsDisposed || window.IsDisposed) return;
            await web.EnsureCoreWebView2Async(environment);
            if (exiting || stop.IsCancellationRequested || web.IsDisposed || window.IsDisposed) return;
            if (web.CoreWebView2 is null) throw new IOException("The desktop browser did not initialize.");
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!IsLocal(e.Uri)) { e.Cancel = true; if (e.Uri == Program.SteamUrl) OpenReference(); }
            };
            web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; if (e.Uri == Program.SteamUrl) OpenReference(); };
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                if (!IsLocal(e.Source)) return;
                try
                {
                    using var data = JsonDocument.Parse(e.WebMessageAsJson);
                    var action = data.RootElement.GetProperty("action").GetString();
                    switch (action)
                    {
                        case "desktop-state": SendDesktopState(); break;
                        case "desktop-startup": SetStartup(data.RootElement.GetProperty("enabled").GetBoolean()); break;
                        case "desktop-close-to-tray": SetMinimizeOnClose(data.RootElement.GetProperty("enabled").GetBoolean()); break;
                        case "desktop-minimize": MinimizeToTray(); break;
                    }
                }
                catch (Exception error) { SendDesktopState(error.Message); }
            };
            web.Source = new Uri(options.BaseUrl + "/#key=" + pairingKey);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (exiting || stop.IsCancellationRequested || window.IsDisposed) return;
            MessageBox.Show(window, "Install Microsoft Edge WebView2 Runtime to use the desktop dashboard. The Steam worker will continue in the tray; the dashboard can also be opened in your browser.", Program.Brand);
            Process.Start(new ProcessStartInfo(options.BaseUrl + "/#key=" + pairingKey) { UseShellExecute = true });
        }
        catch (Exception error) when (IsExpectedCancellation(error)) { }
    }
    private bool IsExpectedCancellation(Exception error) => exiting || stop.IsCancellationRequested || window.IsDisposed || web.IsDisposed || error is OperationCanceledException or ObjectDisposedException || error.HResult == unchecked((int)0x80004004);
    private bool IsLocal(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) == options.BaseUrl;
    private async void ShowWindow()
    {
        if (exiting || window.IsDisposed) return;
        window.Show(); window.WindowState = FormWindowState.Normal; window.Activate();
        if (ready)
        {
            try { await InitializeWebAsync(); }
            catch (Exception error) { if (!IsExpectedCancellation(error)) MessageBox.Show(window, error.Message, Program.Brand); }
        }
    }
    private void MinimizeToTray() { window.Hide(); }
    private void ToggleStartup() { try { SetStartup(!startup.Enabled); } catch (Exception error) { MessageBox.Show(window, error.Message, Program.Brand); } }
    private void SetStartup(bool enabled) { startup.Enabled = enabled; startupItem.Checked = startup.Enabled; SendDesktopState(); }
    private void SetMinimizeOnClose(bool enabled)
    {
        File.WriteAllText(preferencesPath + ".tmp", JsonSerializer.Serialize(new Preferences(enabled)));
        File.Move(preferencesPath + ".tmp", preferencesPath, true);
        minimizeOnClose = enabled; closeItem.Checked = enabled; SendDesktopState();
    }
    private void SendDesktopState(string? error = null)
    {
        web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "desktop-state", startup = startup.Enabled, minimizeOnClose, error }));
    }
    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(options.InstanceName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopToken);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                var command = await reader.ReadLineAsync(stopToken);
                var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.BeginInvoke(() =>
                {
                    try
                    {
                        switch (command)
                        {
                            case "show": ShowWindow(); break;
                            case "minimize": MinimizeToTray(); break;
                            case "startup-on": SetStartup(true); break;
                            case "startup-off": SetStartup(false); break;
                            case "status": case "exit": break;
                            default: throw new ArgumentException("Unknown desktop command.");
                        }
                        response.SetResult(JsonSerializer.Serialize(new { product = Program.Brand, ready, visible = window.Visible, trayVisible = tray.Visible, startup = startup.Enabled, minimizeOnClose, workerOwned = server is { HasExited: false } }));
                    }
                    catch (Exception error) { response.SetResult(JsonSerializer.Serialize(new { error = error.Message })); }
                });
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(await response.Task.WaitAsync(stopToken));
                if (command == "exit") window.BeginInvoke(async () => await QuitAsync());
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (IOException) { }
            catch (Exception error)
            {
                if (stopToken.IsCancellationRequested) break;
                File.AppendAllText(Path.Combine(options.DataDirectory, "desktop.log"), DateTimeOffset.UtcNow.ToString("O") + " IPC: " + error.GetType().Name + Environment.NewLine);
                await Task.Delay(500, stopToken);
            }
        }
    }
    private async Task QuitAsync()
    {
        if (exiting) return;
        exiting = true; ready = false; recovery.Stop();
        if (!web.IsDisposed && web.CoreWebView2 is not null) { try { web.CoreWebView2.Stop(); } catch (Exception) { } }
        try { using var response = await http.PostAsync("/api/shutdown", null); }
        catch (Exception) { }
        if (server is { HasExited: false })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await server.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { server.Kill(); }
        }
        stop.Cancel(); tray.Visible = false; window.Close(); ExitThread();
    }
    private static void OpenReference() => Process.Start(new ProcessStartInfo(Program.SteamUrl) { UseShellExecute = true });
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true; exiting = true; stop.Cancel();
            Application.Idle -= InitializeOnce;
            recovery.Dispose(); tray.Dispose(); web.Dispose(); window.Dispose(); server?.Dispose(); http.Dispose(); stop.Dispose();
        }
        base.Dispose(disposing);
    }
    private sealed record Preferences(bool MinimizeOnClose);
}
