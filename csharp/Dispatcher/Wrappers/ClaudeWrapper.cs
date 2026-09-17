using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LlmDispatcher.Wrappers;

/// <summary>
/// <c>llm-dispatcher claude [claude args...]</c>
///
/// Launches the real Claude Code with a loopback proxy as ANTHROPIC_BASE_URL and a "Dispatcher"
/// row in the /model picker. Claude Code keeps its own login, tools, permissions and sessions.
/// The proxy rewrites only the model and effort fields of requests that name the Dispatcher row,
/// forwards everything else byte-for-byte, and streams responses straight back.
///
/// Anthropic documents this shape: with ANTHROPIC_BASE_URL set and no gateway credential, the
/// saved claude.ai login stays the active credential and its usage limits apply.
/// </summary>
public static partial class ClaudeWrapper
{
    private static readonly string UserSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static async Task<int> RunAsync(string[] args)
    {
        var cli = Launch.ResolveOnPath("claude");
        if (cli is null)
        {
            Console.Error.WriteLine("claude was not found on PATH. Install Claude Code: https://code.claude.com/docs/en/setup");
            return 1;
        }
        var upstream = (Env.Get("ANTHROPIC_BASE_URL") ?? "https://api.anthropic.com").TrimEnd('/');
        var savedModel = ReadSavedModel();
        var router = new TurnRouter(new TurnRouterOptions
        {
            Provider = "anthropic",
            AllowList = Env.Get("DISPATCHER_CLAUDE_MODELS"),
            BaselineModel = Env.Get("ANTHROPIC_MODEL") ?? savedModel,
            StatusFile = TurnRouter.ClaudeStatusFile,
        });

        await using var server = await Loopback.StartAsync(async (ctx, raw) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            var target = $"{upstream}{path}{ctx.Request.QueryString}";
            var isMessages = ctx.Request.Method == "POST" && MessagesPath().IsMatch(path);
            if (!isMessages || raw.Length == 0) { await Loopback.ForwardAsync(target, ctx, raw); return; }

            JsonObject? body;
            try { body = JsonNode.Parse(raw) as JsonObject; }
            catch (JsonException) { body = null; }
            if (body is null || !Turns.IsAutoModel(body["model"], Turns.ClaudeAutoModel))
            {
                Debug($"{path} model={body?["model"]} -> passthrough");
                await Loopback.ForwardAsync(target, ctx, raw);
                return;
            }

            // Auxiliary calls (session titles, summaries) carry no tools; they never need a frontier model.
            if (body["tools"] is not JsonArray { Count: > 0 })
            {
                var cheap = router.Cheapest();
                Turns.ApplyClaudeModel(body, cheap, null);
                Debug($"{path} auxiliary (no tools) -> {cheap.Model}");
                await Loopback.ForwardAsync(target, ctx, Encode(body));
                return;
            }

            var key = Turns.ConversationKey(FirstUserText(body));
            var prompt = Turns.AnthropicNewTurn(body);
            var decision = prompt is not null && path == "/v1/messages"
                ? await router.DecideAsync(key, Turns.AnthropicFeatures(body), ctx.RequestAborted)
                : router.Current(key);
            if (decision is null)
            {
                var spec = router.SafeDefault();
                Turns.ApplyClaudeModel(body, spec, spec.Effort.Contains("high") ? "high" : null);
                Debug($"{path} no decision yet -> safe default {spec.Model}");
            }
            else
            {
                Turns.ApplyClaudeModel(body, decision.Model, decision.Effort);
                Debug($"{path} {(prompt is not null ? "NEW TURN" : "continuation")} -> {decision.Model.Model}{(decision.Effort is null ? "" : $" @{decision.Effort}")} ({decision.Judgment.Source}, task {decision.Task})");
            }
            await Loopback.ForwardAsync(target, ctx, Encode(body));
        });

        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = $"http://127.0.0.1:{server.Port}",
            ["ANTHROPIC_CUSTOM_MODEL_OPTION"] = Turns.ClaudeAutoModel,
            ["ANTHROPIC_CUSTOM_MODEL_OPTION_NAME"] = "Dispatcher",
            ["ANTHROPIC_CUSTOM_MODEL_OPTION_DESCRIPTION"] = "Jev picks the cheapest Claude model and effort for each turn",
            ["ANTHROPIC_CUSTOM_MODEL_OPTION_SUPPORTED_CAPABILITIES"] = "thinking,adaptive_thinking,interleaved_thinking,effort,max_effort",
            ["CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT"] = "1",
        };
        var userNamedModel = args.Any(a => a == "--model" || a.StartsWith("--model=", StringComparison.Ordinal));
        if (!Env.Has("ANTHROPIC_MODEL") && !userNamedModel) env["ANTHROPIC_MODEL"] = Turns.ClaudeAutoModel;

        var extraArgs = StatusLineArgs();
        Console.Error.WriteLine($"[dispatcher] proxy on 127.0.0.1:{server.Port} -> {upstream}; jev {(router.Judge.Enabled ? "on" : "OFF (no TYPESAFE_API_KEY; safe default only)")}; models: {string.Join(", ", router.Models.Select(m => m.Model))}");
        try
        {
            return await Launch.RunChildAsync(cli, [.. extraArgs, .. args], env);
        }
        finally
        {
            RestoreSavedModel(savedModel);
        }
    }

    private static byte[] Encode(JsonObject body) => System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());

    private static void Debug(string msg)
    {
        if (Env.Has("DISPATCHER_DEBUG")) Console.Error.WriteLine($"[dispatcher] {msg}");
    }

    private static string FirstUserText(JsonObject body)
    {
        var first = (body["messages"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => m["role"]?.GetValue<string>() == "user");
        var c = first?["content"];
        var text = c switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonArray arr => string.Join("\n", arr.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "text").Select(b => b["text"]?.GetValue<string>() ?? "")),
            _ => "",
        };
        return text[..Math.Min(text.Length, 2000)];
    }

    /* ---- Claude Code saves a picker choice made with Enter as the default; never leave our sentinel behind ---- */

    public static string? ReadSavedModel(string? file = null)
    {
        try
        {
            var model = (JsonNode.Parse(File.ReadAllText(file ?? UserSettings)) as JsonObject)?["model"]?.GetValue<string>();
            return model == Turns.ClaudeAutoModel ? null : model;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return null; }
    }

    public static bool RestoreSavedModel(string? previous, string? file = null)
    {
        file ??= UserSettings;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject settings) return false;
            if (settings["model"]?.GetValue<string>() != Turns.ClaudeAutoModel) return false;
            if (previous is null) settings.Remove("model"); else settings["model"] = previous;
            File.WriteAllText(file, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Claude Code shows the model it asked for, not the one the proxy chose, so a status line is
    /// the only place the decision can appear. A status line the user configured themselves wins.
    /// </summary>
    private static string[] StatusLineArgs()
    {
        if (Env.Has("DISPATCHER_NO_STATUSLINE")) return [];
        foreach (var dir in new[] { Path.Combine(Directory.GetCurrentDirectory(), ".claude"), Path.GetDirectoryName(UserSettings)! })
        {
            try
            {
                if ((JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json"))) as JsonObject)?["statusLine"] is not null) return [];
            }
            catch (Exception ex) when (ex is IOException or JsonException) { /* nothing to preserve */ }
        }
        var exe = Environment.ProcessPath ?? "llm-dispatcher";
        var command = $"\"{exe}\" statusline";
        var file = Path.Combine(TurnRouter.StatusDir, "claude-settings.json");
        try
        {
            Directory.CreateDirectory(TurnRouter.StatusDir);
            File.WriteAllText(file, new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command } }.ToJsonString());
            return ["--settings", file];
        }
        catch (IOException) { return []; }
    }

    [GeneratedRegex(@"^/v1/messages(/count_tokens)?$")]
    private static partial Regex MessagesPath();
}
