using System.Text.Json;

namespace LlmDispatcher;

/// <summary>Code-side facts about the request. No model involved.</summary>
public static class FeatureExtractor
{
    private const int Recent = 6;
    private const int RecentChars = 1500;
    private const int LastUserChars = 8000;
    private const int SystemChars = 1500;

    public static Features Extract(ChatRequest req)
    {
        var chars = 0;
        var hasImages = false;
        var system = new List<string>();
        var turns = new List<Turn>();

        foreach (var m in req.Messages)
        {
            var text = m.Text();
            chars += text.Length + 16;
            if (m.HasImage()) hasImages = true;
            if (m.ToolCalls is { Count: > 0 }) chars += JsonSerializer.Serialize(m.ToolCalls, Json.Wire).Length;
            if (m.Role is "system" or "developer") system.Add(text);
            else turns.Add(new Turn(m.Role, text));
        }
        if (req.Tools is { Count: > 0 }) chars += JsonSerializer.Serialize(req.Tools, Json.Wire).Length;

        var lastUser = turns.LastOrDefault(t => t.Role == "user")?.Text ?? "";
        return new Features
        {
            InputTokens = (int)Math.Ceiling(chars / 4.0),
            Turns = turns.Count,
            HasImages = hasImages,
            HasTools = req.Tools is { Count: > 0 },
            ToolNames = (req.Tools ?? []).Select(t => t.Function.Name).Where(n => !string.IsNullOrEmpty(n)).Take(40).ToList(),
            RequestedMaxTokens = req.RequestedMaxTokens,
            SystemExcerpt = Truncate(string.Join("\n", system), SystemChars),
            Recent = turns.TakeLast(Recent).Select(t => new Turn(t.Role, Truncate(t.Text, RecentChars))).ToList(),
            LastUser = Truncate(lastUser, LastUserChars),
        };
    }

    public static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = (int)Math.Floor(max * 0.7);
        var tail = max - head;
        return $"{s[..head]}\n…[{s.Length - max} chars omitted]…\n{s[^tail..]}";
    }
}
