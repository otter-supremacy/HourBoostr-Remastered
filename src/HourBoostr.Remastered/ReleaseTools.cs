using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HourBoostr.Remastered;

public static class ReleaseTools
{
    public static string CurrentVersion => typeof(ReleaseTools).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0-preview.1";
    public static async Task<object> CheckAsync(string? repository, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(repository)) return new { configured = false, message = "Set the remaster release repository after publishing it." };
        if (!Regex.IsMatch(repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new ArgumentException("Use a GitHub owner/repository name.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HourBoostr-Remastered/" + CurrentVersion);
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancellation);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new { configured = true, message = "No published release was found." };
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var tag = json.RootElement.GetProperty("tag_name").GetString()!;
        return new { configured = true, currentVersion = CurrentVersion, latestVersion = tag, updateAvailable = IsNewer(tag, CurrentVersion), url = $"https://github.com/{repository}/releases/latest" };
    }
    public static bool IsNewer(string candidate, string current)
    {
        static (Version Version, string? Pre) Parse(string value)
        {
            var pieces = value.TrimStart('v').Split('+')[0].Split('-', 2);
            return (Version.Parse(pieces[0]), pieces.Length == 2 ? pieces[1] : null);
        }
        var a = Parse(candidate); var b = Parse(current);
        var comparison = a.Version.CompareTo(b.Version); if (comparison != 0) return comparison > 0;
        if (a.Pre == b.Pre) return false; if (a.Pre is null) return true; if (b.Pre is null) return false;
        var aa = a.Pre.Split('.'); var bb = b.Pre.Split('.');
        for (var i = 0; i < Math.Min(aa.Length, bb.Length); i++)
        {
            bool an = int.TryParse(aa[i], out int av), bn = int.TryParse(bb[i], out int bv);
            int c = an && bn ? av.CompareTo(bv) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(aa[i], bb[i]);
            if (c != 0) return c > 0;
        }
        return aa.Length > bb.Length;
    }
    public static string Stage(string packagePath, string expectedHash, string stageRoot)
    {
        if (!Regex.IsMatch(expectedHash, "^[a-fA-F0-9]{64}$")) throw new ArgumentException("Supply the release SHA-256 checksum.");
        using (var stream = File.OpenRead(packagePath))
        {
            var actual = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash))) throw new ArgumentException("The package checksum does not match.");
        }
        var destination = Path.GetFullPath(Path.Combine(stageRoot, "staged-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(destination);
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count > 3000 || archive.Entries.Sum(x => x.Length) > 750L * 1024 * 1024) throw new ArgumentException("The release archive is too large.");
            foreach (var entry in archive.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('\\', '/')));
                if (!path.StartsWith(destination + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new ArgumentException("Unsafe path in release archive.");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path);
            }
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "release-manifest.json")));
            if (manifest.RootElement.GetProperty("product").GetString() != "HourBoostr.Remastered") throw new ArgumentException("This package is not an HourBoostr Remastered release.");
            return destination;
        }
        catch { if (Directory.Exists(destination)) Directory.Delete(destination, true); throw; }
    }
    public static void Apply(string stagedPath, string installPath)
    {
        stagedPath = Path.GetFullPath(stagedPath); installPath = Path.GetFullPath(installPath);
        if (Path.TrimEndingDirectorySeparator(installPath) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(installPath)!)) throw new ArgumentException("A drive root cannot be an installation directory.");
        if (stagedPath == installPath || stagedPath.StartsWith(installPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || installPath.StartsWith(stagedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Staging and install directories must be separate.");
        if (!File.Exists(Path.Combine(stagedPath, "release-manifest.json")) || !File.Exists(Path.Combine(installPath, "release-manifest.json"))) throw new ArgumentException("Both paths must be managed release directories; source checkouts cannot be updated this way.");
        ValidateManifest(stagedPath); ValidateManifest(installPath);
        var backup = installPath + ".previous";
        if (Directory.Exists(backup)) throw new ArgumentException("A previous release backup exists. Move it aside before applying another update.");
        var prepared = installPath + ".next-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyRelease(stagedPath, prepared);
            Directory.Move(installPath, backup);
            try { Directory.Move(prepared, installPath); }
            catch { Directory.Move(backup, installPath); throw; }
        }
        finally
        {
            if (Directory.Exists(prepared) && Path.GetFullPath(prepared).StartsWith(installPath + ".next-", StringComparison.Ordinal)) Directory.Delete(prepared, true);
        }
    }
    public static void Rollback(string installPath)
    {
        installPath = Path.GetFullPath(installPath); var previous = installPath + ".previous";
        if (!File.Exists(Path.Combine(previous, "release-manifest.json")) || !File.Exists(Path.Combine(installPath, "release-manifest.json"))) throw new ArgumentException("No managed previous release is available.");
        ValidateManifest(previous); ValidateManifest(installPath);
        var failed = installPath + ".replaced-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Directory.Move(installPath, failed);
        try { Directory.Move(previous, installPath); } catch { Directory.Move(failed, installPath); throw; }
    }
    private static void ValidateManifest(string directory)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "release-manifest.json")));
        if (!manifest.RootElement.TryGetProperty("product", out var product) || product.GetString() != "HourBoostr.Remastered") throw new ArgumentException("The directory is not a managed remaster release.");
    }
    private static void CopyRelease(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Release directories must not contain symbolic links.");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (attributes.HasFlag(FileAttributes.Directory)) CopyRelease(entry, target);
            else
            {
                File.Copy(entry, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(entry));
            }
        }
    }
}
