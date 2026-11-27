using System.Globalization;
using System.Text.Json;

namespace HourBoostr.Remastered;

internal sealed record GroqModelTokens(long InputTokens = 0, long OutputTokens = 0, long TotalTokens = 0, long Responses = 0);

internal sealed class GroqTokenJournal(DataStore store)
{
    private const string Path = "groq-token-usage.json";
    private readonly object gate = new();
    private sealed class Usage
    {
        public Usage() { }
        public string Date { get; set; } = "";
        public Dictionary<string, GroqModelTokens> Models { get; set; } = [];
    }

    public GroqModelTokens[] Snapshot(DateTimeOffset now)
    {
        lock (gate)
        {
            var usage = Read(now);
            return [
                usage.Models.GetValueOrDefault(AiReplyService.Model) ?? new(),
                usage.Models.GetValueOrDefault(AiReplyService.QwenModel) ?? new()
            ];
        }
    }

    public void Record(string model, JsonElement response, DateTimeOffset now)
    {
        if (!response.TryGetProperty("usage", out var element) || element.ValueKind != JsonValueKind.Object
            || !ReadCount(element, "prompt_tokens", out var input)
            || !ReadCount(element, "completion_tokens", out var output)) return;
        var total = ReadCount(element, "total_tokens", out var reported) ? reported : input + output;
        lock (gate)
        {
            var usage = Read(now);
            var old = usage.Models.GetValueOrDefault(model) ?? new();
            usage.Models[model] = new(old.InputTokens + input, old.OutputTokens + output, old.TotalTokens + total, old.Responses + 1);
            try { store.Write(Path, usage); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private Usage Read(DateTimeOffset now)
    {
        var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            var saved = store.Read<Usage>(Path);
            if (saved?.Date == date) return saved;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        return new() { Date = date };
    }

    private static bool ReadCount(JsonElement source, string name, out long count)
    {
        count = 0;
        return source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out count) && count is >= 0 and <= 10_000_000;
    }
}
