using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmDispatcher.Wrappers;

/// <summary>
/// <c>llm-dispatcher statusline</c>: Claude Code runs this and pipes session JSON on stdin.
/// Prints the last routing decision so the user can see which model actually served the turn.
/// </summary>
public static class Statusline
{
    public static async Task RunAsync()
    {
        JsonObject input = new();
        try
        {
            if (Console.IsInputRedirected)
            {
                var raw = await Console.In.ReadToEndAsync();
                if (raw.Trim().Length > 0) input = JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
            }
        }
        catch (JsonException) { /* no usable stdin */ }

        var cwd = (input["workspace"] as JsonObject)?["current_dir"]?.GetValue<string>() ?? input["cwd"]?.GetValue<string>() ?? Directory.GetCurrentDirectory();
        var pct = (input["context_window"] as JsonObject)?["used_percentage"] is JsonValue p && p.TryGetValue<double>(out var d) ? d : (double?)null;
        var requested = (input["model"] as JsonObject)?["display_name"]?.GetValue<string>() ?? "";
        var parts = new List<string>();

        JsonObject? status = null;
        try { status = JsonNode.Parse(File.ReadAllText(TurnRouter.ClaudeStatusFile)) as JsonObject; }
        catch (Exception ex) when (ex is IOException or JsonException) { /* no decision yet */ }

        if (requested.Length > 0 && !requested.Contains("dispatcher", StringComparison.OrdinalIgnoreCase))
            parts.Add($"⏸ manual {requested}");
        else if (status is not null)
        {
            var model = (status["model"]?.GetValue<string>() ?? "").Replace("anthropic/", "");
            var effort = status["effort"]?.GetValue<string>();
            var conf = (status["confidence"] as JsonObject)?["difficulty"]?.GetValue<double>();
            var source = status["source"]?.GetValue<string>();
            var src = source == "jev" ? $"jev{(conf is { } c ? $" p={c:F2}" : "")}" : source ?? "";
            parts.Add($"⚡ {model}{(effort is null ? "" : $" @{effort}")} ({status["task"] ?? "?"}, {src})");
        }
        else parts.Add("⚡ dispatcher: waiting for first turn");

        parts.Add(Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar)));
        if (pct is { } used) parts.Add($"{Math.Round(used)}% context");
        Console.Out.Write(string.Join(" · ", parts));
    }
}
