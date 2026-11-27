using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HourBoostr.Members;

internal sealed record Credentials(string? Email, string? Password, string? RecoveryCode);

internal sealed class PasswordAccounts
{
    private readonly PasswordHasher<string> hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 220_000 }));
    private readonly string dummyHash;
    private readonly SemaphoreSlim hashSlots = new(2);

    public PasswordAccounts() => dummyHash = hasher.HashPassword("", NewRecoveryCode());

    public async Task<IResult> Handle(string operation, Credentials input, HttpContext context, MemberStore store,
        Func<(string Member, string Session), System.Security.Claims.ClaimsPrincipal> principal)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            return Results.Conflict(new { error = "Sign out before switching accounts." });

        var address = input.Email?.Trim();
        if (address is null || address.Length > 254 || address.Any(c => c > 127)
            || !MailAddress.TryCreate(address, out var valid) || valid.Address != address || !valid.Host.Contains('.'))
            return Error("Enter a valid email address.");
        var email = address.ToLowerInvariant();
        var password = input.Password;
        if (password is null || password.Length is < 15 or > 128)
            return Error(operation == "login" ? "Email or password is incorrect, or sign-in is locked. Try again later." : "Use 15 to 128 characters for your password.");

        var normalizedCode = (input.RecoveryCode ?? "").Trim().Replace("-", "").ToUpperInvariant();
        if (operation == "recover" && (normalizedCode.Length != 32 || !normalizedCode.All(Uri.IsHexDigit)))
            return Error("Enter your 32-character recovery code.");

        if (!await hashSlots.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
        (string Member, string Session)? session;
        string? recoveryCode = null;
        try
        {
            if (operation == "login")
            {
                var hash = store.PasswordHash(email);
                var verified = hasher.VerifyHashedPassword(email, hash ?? dummyHash, password) != PasswordVerificationResult.Failed;
                session = hash is null ? null : store.CompletePasswordLogin(email, hash, verified);
            }
            else
            {
                var hash = hasher.HashPassword(email, password);
                recoveryCode = NewRecoveryCode();
                var recoveryHash = CodeHash(recoveryCode);
                session = operation == "register"
                    ? store.RegisterPassword(email, hash, recoveryHash)
                    : store.RecoverPassword(email, CodeHash(normalizedCode), hash, recoveryHash);
            }
        }
        finally { hashSlots.Release(); }

        if (session is null)
            return operation switch
            {
                "register" => Results.Conflict(new { error = "Could not create the account. If you already registered with email, sign in or use your recovery code." }),
                "recover" => Error("That email and recovery code could not be used."),
                _ => Error("Email or password is incorrect, or sign-in is locked. Try again later.")
            };

        await context.SignInAsync(principal(session.Value));
        return recoveryCode is null ? Results.NoContent() : Results.Ok(new { recoveryCode });
    }

    private static string NewRecoveryCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private static string CodeHash(string code) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(code)));
    private static IResult Error(string message) => Results.BadRequest(new { error = message });
}
