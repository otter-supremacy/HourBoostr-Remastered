using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HourBoostr.Remastered;
using Xunit;

namespace HourBoostr.Remastered.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hbr-tests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void AccountSettingsPersistIndependentlyFromSessions()
    {
        var store = new DataStore(root); var a = store.Add("First"); var b = store.Add("Second");
        var vault = new SecretVault(store); vault.Save(a.Id, new("test-login", "TEST_REFRESH_VALUE", "TEST_GUARD_VALUE"));
        a.Paused = true; store.Save(a);
        var reopened = new DataStore(root);
        Assert.True(reopened.Account(a.Id)!.Paused); Assert.False(reopened.Account(b.Id)!.Paused);
        Assert.DoesNotContain("TEST_REFRESH_VALUE", File.ReadAllText(store.Resolve("settings.json")));
        Assert.Equal("TEST_REFRESH_VALUE", vault.Get(a.Id)!.RefreshToken);
        if (OperatingSystem.IsWindows()) Assert.DoesNotContain("TEST_REFRESH_VALUE", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.Resolve($"session-{a.Id}.secret"))));
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Resolve($"session-{a.Id}.secret")));
        vault.Forget(a.Id); Assert.Null(vault.Get(a.Id));
    }
    [Fact]
    public void ReleaseSourcePersistsWhenAccountChanges()
    {
        var store = new DataStore(root); var account = store.Add("Account"); store.SetReleaseRepository("example/remaster"); account.Paused = true; store.Save(account);
        Assert.Equal("example/remaster", new DataStore(root).Settings.ReleaseRepository);
    }
    [Fact]
    public void ProtectedAccountsSurviveConfigurationChanges()
    {
        var store = new DataStore(root); store.ProtectAccounts(["protected-test-login"]);
        store.SetReleaseRepository("example/remaster"); var account = store.Add("Test"); account.Paused = true; store.Save(account);
        Assert.Contains("protected-test-login", new DataStore(root).Settings.ProtectedLoginNames);
        Assert.DoesNotContain("protected-test-login", JsonSerializer.Serialize(Diagnostics.Report(store)));
    }
    [Fact]
    public void CorruptSessionDoesNotCrashTheDashboard()
    {
        var store = new DataStore(root); var account = store.Add("Test");
        File.WriteAllText(store.Resolve($"session-{account.Id}.secret"), "invalid session data");
        Assert.Null(new SecretVault(store).Get(account.Id));
    }
    [Fact]
    public void UnknownFutureSchemaIsNotOverwritten()
    {
        Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "settings.json"), "{\"schemaVersion\":999,\"accounts\":[]}");
        Assert.Throws<ArgumentException>(() => new DataStore(root));
        Assert.Contains("999", File.ReadAllText(Path.Combine(root, "settings.json")));
    }
    [Fact]
    public void DataPathsCannotEscapeTheirDirectory()
    { var store = new DataStore(root); Assert.Throws<ArgumentException>(() => store.Resolve("../secrets")); }
    [Fact]
    public void DashboardCookiesCannotBeForgedAndExpire()
    {
        var security = new DashboardSecurity(new SecretVault(new DataStore(root))); var now = DateTimeOffset.UtcNow;
        var cookie = security.CreateCookie(now);
        Assert.True(security.ValidateCookie(cookie, now));
        Assert.False(security.ValidateCookie(cookie + "0", now));
        Assert.False(security.ValidateCookie(cookie, now.AddDays(31)));
        Assert.False(security.Matches("incorrect"));
    }
    [Fact]
    public void DiagnosticsDoNotExportIdentitiesOrTokens()
    {
        var store = new DataStore(root); var config = store.Add("PRIVATE_LABEL"); config.LoginName = "PRIVATE_LOGIN"; store.Save(config);
        new SecretVault(store).Save(config.Id, new("PRIVATE_LOGIN", "PRIVATE_REFRESH", "PRIVATE_GUARD"));
        var report = JsonSerializer.Serialize(Diagnostics.Report(store));
        Assert.DoesNotContain("PRIVATE_LOGIN", report); Assert.DoesNotContain("PRIVATE_REFRESH", report); Assert.DoesNotContain("PRIVATE_GUARD", report); Assert.DoesNotContain("PRIVATE_LABEL", report);
    }
    private string Archive(bool traversal)
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "release.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("release-manifest.json").Open())) writer.Write("{\"product\":\"HourBoostr.Remastered\",\"version\":\"1.0.0\"}");
        using (var writer = new StreamWriter(zip.CreateEntry(traversal ? "../escape.txt" : "HourBoostr.Remastered.dll").Open())) writer.Write("TEST_BINARY");
        return path;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    [Fact]
    public void UpdateRejectsWrongHashAndZipTraversal()
    {
        var archive = Archive(true);
        Assert.Throws<ArgumentException>(() => ReleaseTools.Stage(archive, new string('0', 64), Path.Combine(root, "updates")));
        Assert.Throws<ArgumentException>(() => ReleaseTools.Stage(archive, Hash(archive), Path.Combine(root, "updates")));
        Assert.False(File.Exists(Path.Combine(root, "updates", "escape.txt")));
    }
    [Fact]
    public void VerifiedUpdateCanBeAppliedAndRolledBack()
    {
        var archive = Archive(false); var staged = ReleaseTools.Stage(archive, Hash(archive), Path.Combine(root, "updates"));
        var install = Path.Combine(root, "app"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "release-manifest.json"), "{\"product\":\"HourBoostr.Remastered\"}");
        File.WriteAllText(Path.Combine(install, "old.txt"), "old");
        ReleaseTools.Apply(staged, install); Assert.True(File.Exists(Path.Combine(install, "HourBoostr.Remastered.dll")));
        ReleaseTools.Rollback(install); Assert.True(File.Exists(Path.Combine(install, "old.txt")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
