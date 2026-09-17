using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LlmDispatcher.Wrappers;

/// <summary>
/// Pure functions that understand what the two CLIs send. They decide whether a request opens a
/// fresh user turn, build routing features from it, and rewrite it for the chosen model.
/// Mirrors ts/src/wrappers/turns.ts; nothing here performs I/O.
/// </summary>
public static partial class Turns
{
    public const string ClaudeAutoModel = "dispatcher-auto";
    public const string CodexAutoModel = "dispatcher-auto";

    private static readonly HashSet<string> AnthropicEffort = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Claude Code and Codex inject bookkeeping blocks into user text; they blunt Jev's read of the prompt.</summary>
    public static string CleanPrompt(string text) =>
        CurrentDatetime().Replace(SystemReminder().Replace(text, ""), "").Trim();

    public static string ConversationKey(params string?[] parts)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts.Select(p => p ?? ""))));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    /// <summary>True when the model field asks the dispatcher to choose, with or without Claude Code's <c>[1m]</c> suffix.</summary>
    public static bool IsAutoModel(JsonNode? model, string auto)
    {
        if (model is not JsonValue v || !v.TryGetValue<string>(out var s)) return false;
        return s == auto || s.StartsWith(auto + "[", StringComparison.Ordinal);
    }

    /* ------------------------------ Anthropic Messages (Claude Code) ------------------------------ */

    private static string AnthropicText(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is not JsonArray arr) return "";
        return string.Join("\n", arr.OfType<JsonObject>()
            .Where(b => b["type"]?.GetValue<string>() == "text")
            .Select(b => b["text"]?.GetValue<string>() ?? "")
            .Where(t => t.Length > 0));
    }

    private static bool AnthropicHasImage(JsonNode? content) =>
        content is JsonArray arr && arr.OfType<JsonObject>().Any(b => b["type"]?.GetValue<string>() == "image");

    /// <summary>
    /// The cleaned text of a genuinely new user turn, or null. Tool-result continuations reuse the
    /// turn's decision; tool-less requests are auxiliary (titles, summaries); trailing operator
    /// <c>system</c> messages appended by Claude Code are skipped.
    /// </summary>
    public static string? AnthropicNewTurn(JsonObject body)
    {
        if (body["tools"] is not JsonArray tools || tools.Count == 0) return null;
        if (body["messages"] is not JsonArray messages || messages.Count == 0) return null;
        var last = messages.OfType<JsonObject>().LastOrDefault(m => m["role"]?.GetValue<string>() != "system");
        if (last is null || last["role"]?.GetValue<string>() != "user") return null;
        if (last["content"] is JsonArray blocks && blocks.OfType<JsonObject>().Any(b => b["type"]?.GetValue<string>() == "tool_result")) return null;
        var text = CleanPrompt(AnthropicText(last["content"]));
        return text.Length > 0 ? text : null;
    }

    public static Features AnthropicFeatures(JsonObject body)
    {
        var messages = (body["messages"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        string system;
        if (body["system"] is JsonArray sysArr)
            system = string.Join("\n", sysArr.OfType<JsonObject>()
                .Select((b, i) => (text: b["text"]?.GetValue<string>() ?? "", i))
                // Claude Code's first system block is a version/fingerprint attribution line, not instructions.
                .Where(x => !(x.i == 0 && Attribution().IsMatch(x.text)))
                .Select(x => x.text));
        else system = body["system"]?.GetValue<string>() ?? "";

        var turns = messages
            .Where(m => m["role"]?.GetValue<string>() != "system")
            .Select(m => new Turn(m["role"]?.GetValue<string>() ?? "", CleanPrompt(AnthropicText(m["content"]))))
            .Where(t => t.Text.Length > 0).ToList();
        var lastUser = turns.LastOrDefault(t => t.Role == "user")?.Text ?? "";
        var tools = (body["tools"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        return new Features
        {
            InputTokens = (int)Math.Ceiling(body.ToJsonString().Length / 4.0),
            Turns = messages.Count,
            HasImages = messages.Any(m => AnthropicHasImage(m["content"])),
            HasTools = tools.Count > 0,
            ToolNames = tools.Select(t => t["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).Take(40).ToList(),
            RequestedMaxTokens = body["max_tokens"] is JsonValue mt && mt.TryGetValue<int>(out var max) ? max : null,
            SystemExcerpt = FeatureExtractor.Truncate(system, 1500),
            Recent = turns.TakeLast(6).Select(t => new Turn(t.Role, FeatureExtractor.Truncate(t.Text, 1500))).ToList(),
            LastUser = FeatureExtractor.Truncate(lastUser, 8000),
        };
    }

    /// <summary>Mid-conversation system messages are accepted by Opus 5, Opus 4.8 and Fable / Mythos only.</summary>
    public static bool SupportsMidConversationSystem(string model) => MidSystemModels().IsMatch(model);

    /// <summary>
    /// Point a Claude Code request at <paramref name="spec"/>, removing fields that model cannot
    /// accept. Routing down to Haiku while leaving adaptive thinking in place would be a hard 400.
    /// </summary>
    public static JsonObject ApplyClaudeModel(JsonObject body, ModelSpec spec, string? effort)
    {
        body["model"] = spec.Model;
        var supportsEffort = spec.Effort.Count > 0;
        var wantEffort = supportsEffort && effort is not null && AnthropicEffort.Contains(effort) && spec.Effort.Contains(effort) ? effort : null;

        if (body["messages"] is JsonArray messages)
        {
            var list = messages.OfType<JsonObject>().ToList();
            if (SupportsMidConversationSystem(spec.Model))
            {
                foreach (var m in list.Where(m => m["role"]?.GetValue<string>() == "system"))
                {
                    if (m["output_config"] is not JsonObject oc || oc["effort"] is null) continue;
                    if (wantEffort is not null) oc["effort"] = wantEffort;
                    else
                    {
                        oc.Remove("effort");
                        if (oc.Count == 0) m.Remove("output_config");
                    }
                }
                var kept = list.Where(m => !(m["role"]?.GetValue<string>() == "system" && m["content"] is JsonArray { Count: 0 } && m["output_config"] is null)).ToList();
                body["messages"] = Rebuild(kept);
            }
            else
            {
                var extra = list.Where(m => m["role"]?.GetValue<string>() == "system").Select(m => AnthropicText(m["content"])).Where(t => t.Length > 0).ToList();
                if (extra.Count > 0)
                {
                    var system = body["system"] switch
                    {
                        JsonArray arr => arr.OfType<JsonObject>().Select(b => (JsonNode)b.DeepClone()).ToList(),
                        JsonValue v when v.TryGetValue<string>(out var s) => [new JsonObject { ["type"] = "text", ["text"] = s }],
                        _ => new List<JsonNode>(),
                    };
                    system.AddRange(extra.Select(t => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = t }));
                    body["system"] = new JsonArray(system.ToArray());
                }
                body["messages"] = Rebuild(list.Where(m => m["role"]?.GetValue<string>() != "system").ToList());
            }
        }

        if (!supportsEffort)
        {
            body.Remove("thinking");
            if (body["context_management"] is JsonObject cm && cm["edits"] is JsonArray edits)
            {
                var kept = edits.OfType<JsonObject>().Where(e => !(e["type"]?.GetValue<string>() ?? "").StartsWith("clear_thinking", StringComparison.Ordinal)).ToList();
                if (kept.Count == 0) body.Remove("context_management");
                else cm["edits"] = Rebuild(kept);
            }
        }
        var outCfg = body["output_config"] as JsonObject ?? new JsonObject();
        if (body["output_config"] is JsonObject existing) existing.Parent?.AsObject().Remove("output_config");
        if (wantEffort is not null) outCfg["effort"] = wantEffort; else outCfg.Remove("effort");
        if (outCfg.Count > 0) body["output_config"] = outCfg;
        return body;
    }

    /* ------------------------------ OpenAI Responses (Codex) ------------------------------ */

    private static readonly HashSet<string> ResponsesTextTypes = ["text", "input_text", "output_text"];

    private static string ResponsesText(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is not JsonArray arr) return "";
        return string.Join("\n", arr.OfType<JsonObject>()
            .Where(p => ResponsesTextTypes.Contains(p["type"]?.GetValue<string>() ?? ""))
            .Select(p => p["text"]?.GetValue<string>() ?? "")
            .Where(t => t.Length > 0));
    }

    /// <summary>User text that starts a new Codex turn, or null for tool-output continuations.</summary>
    public static string? CodexNewTurn(JsonObject body)
    {
        if (body["input"] is not JsonArray input) return null;
        foreach (var item in input.OfType<JsonObject>().Reverse())
        {
            var type = item["type"]?.GetValue<string>();
            if (type is "function_call_output" or "custom_tool_call_output") return null;
            if (item["role"]?.GetValue<string>() != "user") continue;
            var text = CleanPrompt(ResponsesText(item["content"]));
            if (text.Length > 0) return text;
        }
        return null;
    }

    public static Features CodexFeatures(JsonObject body)
    {
        var input = (body["input"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var turns = input.Where(i => i["role"] is not null)
            .Select(i => new Turn(i["role"]!.GetValue<string>(), CleanPrompt(ResponsesText(i["content"]))))
            .Where(t => t.Text.Length > 0).ToList();
        var lastUser = turns.LastOrDefault(t => t.Role == "user")?.Text ?? "";
        var tools = (body["tools"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var hasImages = input.Any(i => i["content"] is JsonArray c && c.OfType<JsonObject>().Any(p => p["type"]?.GetValue<string>() == "input_image"));
        return new Features
        {
            InputTokens = (int)Math.Ceiling(body.ToJsonString().Length / 4.0),
            Turns = turns.Count,
            HasImages = hasImages,
            HasTools = tools.Count > 0,
            ToolNames = tools.Select(t => t["name"]?.GetValue<string>() ?? t["type"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).Take(40).ToList(),
            RequestedMaxTokens = body["max_output_tokens"] is JsonValue mt && mt.TryGetValue<int>(out var max) ? max : null,
            SystemExcerpt = FeatureExtractor.Truncate(body["instructions"]?.GetValue<string>() ?? "", 1500),
            Recent = turns.TakeLast(6).Select(t => new Turn(t.Role, FeatureExtractor.Truncate(t.Text, 1500))).ToList(),
            LastUser = FeatureExtractor.Truncate(lastUser, 8000),
        };
    }

    /// <summary>Codex pins a conversation with <c>prompt_cache_key</c>; older builds only via turn metadata or content.</summary>
    public static string CodexConversationKey(JsonObject body)
    {
        if (body["prompt_cache_key"] is JsonValue pck && pck.TryGetValue<string>(out var key) && key.Length > 0) return ConversationKey(key);
        if (body["client_metadata"] is JsonObject meta && meta["x-codex-turn-metadata"] is JsonValue tm && tm.TryGetValue<string>(out var turnMeta) && turnMeta.Length > 0)
            return ConversationKey(turnMeta);
        var firstUser = (body["input"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(i => i["role"]?.GetValue<string>() == "user");
        var instructions = body["instructions"]?.GetValue<string>() ?? "";
        return ConversationKey(instructions[..Math.Min(instructions.Length, 2000)], firstUser is null ? null : ResponsesText(firstUser["content"]) is var t ? t[..Math.Min(t.Length, 2000)] : null);
    }

    /// <summary>Point a Codex Responses request at <paramref name="spec"/> and set the reasoning effort it supports.</summary>
    public static JsonObject ApplyCodexModel(JsonObject body, ModelSpec spec, string? effort)
    {
        body["model"] = spec.Model;
        // OpenAI rejects `minimal` alongside built-in tools such as web_search, which Codex always attaches.
        if (effort is "minimal" or "none" && spec.Effort.Contains("low")) effort = "low";
        if (effort is not null && spec.Effort.Contains(effort))
        {
            var reasoning = body["reasoning"] as JsonObject ?? new JsonObject();
            if (body["reasoning"] is JsonObject) body.Remove("reasoning");
            reasoning["effort"] = effort;
            body["reasoning"] = reasoning;
        }
        else if (spec.Effort.Count == 0) body.Remove("reasoning");
        return body;
    }

    /// <summary>JsonNodes can only have one parent; detach before re-adding to a new array.</summary>
    private static JsonArray Rebuild(IEnumerable<JsonObject> items)
    {
        var arr = new JsonArray();
        foreach (var item in items)
        {
            item.Parent?.AsArray().Remove(item);
            arr.Add(item);
        }
        return arr;
    }

    [GeneratedRegex(@"<system[-_]reminder>[\s\S]*?</system[-_]reminder>", RegexOptions.IgnoreCase)]
    private static partial Regex SystemReminder();

    [GeneratedRegex(@"<current_datetime>[\s\S]*?</current_datetime>", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentDatetime();

    [GeneratedRegex("^x-anthropic-billing|^claude-code", RegexOptions.IgnoreCase)]
    private static partial Regex Attribution();

    [GeneratedRegex("opus-5|opus-4-8|fable|mythos")]
    private static partial Regex MidSystemModels();
}
