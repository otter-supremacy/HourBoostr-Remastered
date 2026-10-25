using System.Security.Cryptography;
using System.Text;

namespace HourBoostr.Remastered;

public sealed class DashboardSecurity
{
    private readonly string key;
    public DashboardSecurity(SecretVault vault)
    {
        key = vault.Get("dashboard")?.RefreshToken ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        vault.Save("dashboard", new("dashboard", key, null));
    }
    public string Key => key;
    public bool Matches(string? candidate)
    {
        if (candidate is null || candidate.Length != key.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(key));
    }
    public string CreateCookie(DateTimeOffset now)
    {
        var payload = now.AddDays(30).ToUnixTimeSeconds() + ":" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "." + Sign(payload);
    }
    private string Sign(string payload) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload)));
    public bool ValidateCookie(string? cookie, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length > 300) return false;
        var parts = cookie.Split('.'); if (parts.Length != 2) return false;
        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            var expiration = payload.Split(':')[0];
            var expected = Sign(payload);
            return parts[1].Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(parts[1]), Encoding.UTF8.GetBytes(expected))
                && long.TryParse(expiration, out var seconds) && seconds > now.ToUnixTimeSeconds() && seconds <= now.AddDays(31).ToUnixTimeSeconds();
        }
        catch (FormatException) { return false; }
    }
}
