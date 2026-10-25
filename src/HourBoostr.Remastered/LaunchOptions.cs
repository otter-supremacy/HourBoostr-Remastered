using System.Net;

namespace HourBoostr.Remastered;

public sealed class LaunchOptions
{
    public string DataDirectory { get; init; } = DataStore.DefaultPath();
    public int Port { get; init; } = 3241;
    public string Listen { get; init; } = "127.0.0.1";
    public bool Remote { get; init; }
    public string? Certificate { get; init; }
    public string? CertificatePassword { get; init; }
    public string? PublicHost { get; init; }
    public string[] ProtectedNames { get; init; } = [];
    public static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException($"Supply a value for {name}.");
        return args[index + 1];
    }
    public static LaunchOptions Parse(string[] args)
    {
        var portText = Option(args, "--port") ?? Environment.GetEnvironmentVariable("HBR_PORT") ?? "3241";
        if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535) throw new ArgumentException("Port must be between 1024 and 65535.");
        var listen = Option(args, "--listen") ?? "127.0.0.1";
        if (!IPAddress.TryParse(listen, out var address)) throw new ArgumentException("Listen must be an IP address.");
        bool remote = !IPAddress.IsLoopback(address);
        var cert = Option(args, "--cert"); var publicHost = Option(args, "--public-host");
        if (remote && (!args.Contains("--allow-remote") || cert is null || string.IsNullOrWhiteSpace(publicHost))) throw new ArgumentException("Remote access requires --allow-remote, --cert, and --public-host. Loopback is the default.");
        if (publicHost is not null && Uri.CheckHostName(publicHost) == UriHostNameType.Unknown) throw new ArgumentException("Invalid public hostname.");
        return new()
        {
            DataDirectory = Option(args, "--data-dir") ?? Environment.GetEnvironmentVariable("HBR_DATA_DIR") ?? DataStore.DefaultPath(),
            Port = port, Listen = listen, Remote = remote, Certificate = cert, PublicHost = publicHost,
            CertificatePassword = Environment.GetEnvironmentVariable(Option(args, "--cert-password-env") ?? "HBR_CERT_PASSWORD"),
            ProtectedNames = (Environment.GetEnvironmentVariable("HBR_PROTECTED_LOGIN_NAMES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
    }
    public string BaseUrl => $"{(Remote ? "https" : "http")}://{(Remote ? PublicHost : Listen.Contains(':') ? "[" + Listen + "]" : Listen)}:{Port}";
}
