using SteamKit2.Authentication;

namespace HourBoostr.Remastered;

public sealed class DashboardAuthenticator(Action<string, string> prompt, CancellationToken cancellation) : IAuthenticator
{
    private TaskCompletionSource<string>? pending;
    private int attempts;
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => Ask("waiting_guard", previousCodeWasIncorrect ? "The previous code was rejected. Enter a fresh Steam Guard code." : "Enter the code from Steam Mobile.");
    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => Ask("waiting_guard", previousCodeWasIncorrect ? "The previous code was rejected. Enter the latest email code." : "Enter the Steam Guard code sent to your email.");
    public Task<bool> AcceptDeviceConfirmationAsync()
    { prompt("waiting_approval", "Approve this sign-in in Steam Mobile, or enter a Steam Guard code if requested."); return Task.FromResult(true); }
    private async Task<string> Ask(string state, string message)
    {
        if (++attempts > 4) throw new InvalidOperationException("Too many rejected Guard codes. Start a new sign-in.");
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously); prompt(state, message);
        return await pending.Task.WaitAsync(TimeSpan.FromMinutes(4), cancellation);
    }
    public bool Submit(string code)
    {
        code = code.Trim();
        if (code.Length is < 4 or > 10 || !code.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("Enter a valid Steam Guard code.");
        return pending?.TrySetResult(code) ?? false;
    }
}
