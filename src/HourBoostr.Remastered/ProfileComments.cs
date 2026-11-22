using System.Text;
using System.Text.RegularExpressions;

namespace HourBoostr.Remastered;

public sealed record ProfileCommentInput(string Id, string Author, DateTimeOffset At, string Text);
public sealed record ProfileCommentItem(string Id, string Author, DateTimeOffset At, string Text, string Verdict);
public sealed record ProfileCommentSettings(bool Enabled);
public sealed record ProfileCommentDismissal(string Id);

public sealed partial class ProfileComments(DataStore store, string accountId)
{
    private readonly object gate = new();
    private readonly string file = $"profile-comments-{accountId}.json";
    private State? value;

    public sealed class State
    {
        public string Owner { get; set; } = "";
        public bool Enabled { get; set; }
        public DateTimeOffset StartAt { get; set; }
        public DateTimeOffset? CheckedAt { get; set; }
        public List<string> Seen { get; set; } = [];
        public List<ProfileCommentItem> Items { get; set; } = [];
        public int Blocked { get; set; }
    }

    public static bool ContainsLink(string text)
    {
        var normalized = string.Concat(text.Normalize(NormalizationForm.FormKC)
            .Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format))
            .Replace('。', '.').Replace('｡', '.').Replace('．', '.');
        return LinkPattern().IsMatch(normalized);
    }

    [GeneratedRegex(@"(?:\b(?:https?|hxxps?|ftp|mailto|steam|javascript)\s*[:/]|\bwww\s*[.]|\b(?:\d{1,3}[.]){3}\d{1,3}\b|\b[\p{L}\p{N}-]+(?:[.]|\s+[.]\s+|\s*[\[(]\s*(?:dot|[.])\s*[\])])(?:[\p{L}]{2,63}|xn--[a-z0-9-]+)\b|\[(?:url|link|img)(?:=|\]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    private State For(string owner)
    {
        value ??= store.Read<State>(file) ?? new();
        if (value.Owner != owner) value = new() { Owner = owner };
        return value;
    }

    public bool Enabled(string owner)
    {
        lock (gate) return For(owner).Enabled;
    }

    public void Configure(string owner, bool enabled, DateTimeOffset now)
    {
        lock (gate)
        {
            var state = For(owner);
            if (state.Enabled == enabled) return;
            state.Enabled = enabled;
            state.StartAt = now;
            state.CheckedAt = null;
            state.Seen.Clear();
            state.Items.Clear();
            state.Blocked = 0;
            store.Write(file, state);
        }
    }

    public object Read(string owner, DateTimeOffset now, string status)
    {
        lock (gate)
        {
            var state = For(owner);
            if (state.Items.RemoveAll(item => item.At < now.AddDays(-7)) > 0) store.Write(file, state);
            return new { state.Enabled, state.CheckedAt, state.Blocked, status, items = state.Items.OrderByDescending(item => item.At).ToArray() };
        }
    }

    public void Dismiss(string owner, string id)
    {
        lock (gate)
        {
            var state = For(owner);
            if (state.Items.RemoveAll(item => item.Id == id) > 0) store.Write(file, state);
        }
    }

    public async Task ObserveAsync(string owner, IEnumerable<ProfileCommentInput> comments, DateTimeOffset now,
        Func<string, CancellationToken, Task<string>> screen, CancellationToken token)
    {
        DateTimeOffset startAt;
        List<ProfileCommentInput> pending;
        lock (gate)
        {
            var state = For(owner);
            if (!state.Enabled) return;
            startAt = state.StartAt;
            pending = comments.Where(comment => comment.At > startAt && comment.At >= now.AddDays(-7)
                && comment.At <= now.AddSeconds(10) && !state.Seen.Contains(comment.Id))
                .DistinctBy(comment => comment.Id).OrderBy(comment => comment.At).Take(10).ToList();
            state.CheckedAt = now;
            if (state.Items.RemoveAll(item => item.At < now.AddDays(-7)) > 0) store.Write(file, state);
        }

        foreach (var comment in pending)
        {
            token.ThrowIfCancellationRequested();
            if (!Enabled(owner)) return;
            var verdict = comment.Text.Length > 1000 || ContainsLink(comment.Text)
                ? "block" : await screen(comment.Text, token);
            lock (gate)
            {
                var state = For(owner);
                if (!state.Enabled || state.StartAt != startAt || state.Seen.Contains(comment.Id)) return;
                state.Seen.Add(comment.Id);
                if (state.Seen.Count > 512) state.Seen.RemoveAt(0);
                if (verdict == "block") state.Blocked++;
                else
                {
                    state.Items.Add(new(comment.Id, comment.Author, comment.At, comment.Text, verdict == "safe" ? "safe" : "review"));
                    if (state.Items.Count > 40) state.Items.RemoveAt(0);
                }
                store.Write(file, state);
            }
        }
    }
}
