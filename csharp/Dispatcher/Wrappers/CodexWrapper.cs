using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmDispatcher.Wrappers;

/// <summary>
/// <c>llm-dispatcher codex [codex args...]</c>
///
/// Launches the real Codex CLI with a temporary model provider that points at a loopback proxy
/// and reuses Codex's own OpenAI login (<c>requires_openai_auth</c>, a documented provider option).
/// The proxy rewrites model and reasoning effort on fresh user turns that name the Dispatcher
/// model, forwards everything else unchanged, and streams responses back.
/// </summary>
public static class CodexWrapper
{
    private const string ChatGptUpstream = "https://chatgpt.com/backend-api/codex";
    private const string ApiUpstream = "https://api.openai.com/v1";
    private const string ProviderId = "dispatcher";

    public static async Task<int> RunAsync(string[] args)
    {
        var cli = Launch.ResolveOnPath("codex");
        if (cli is null)
        {
            Console.Error.WriteLine("codex was not found on PATH. Install Codex: https://developers.openai.com/codex/cli");
            return 1;
        }
        var router = new TurnRouter(new TurnRouterOptions
        {
            Provider = "openai",
            AllowList = Env.Get("DISPATCHER_CODEX_MODELS") ?? "gpt-5.6-luna,gpt-5.6-terra,gpt-5.6-sol,gpt-6-astra",
            BaselineModel = Env.Get("DISPATCHER_CODEX_BASELINE") ?? "gpt-6-astra",
            StatusFile = TurnRouter.CodexStatusFile,
        });

        await using var server = await Loopback.StartAsync(async (ctx, raw) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            // Codex sends a ChatGPT account header when signed in with a subscription; API-key logins go to the platform API.
            var upstream = Env.Get("DISPATCHER_CODEX_UPSTREAM") ?? (ctx.Request.Headers.ContainsKey("chatgpt-account-id") ? ChatGptUpstream : ApiUpstream);
            var target = $"{upstream}{path}{ctx.Request.QueryString}";

            if (ctx.Request.Method == "GET" && path == "/models") { await ForwardModelsAsync(target, ctx); return; }
            if (!(ctx.Request.Method == "POST" && path == "/responses") || raw.Length == 0) { await Loopback.ForwardAsync(target, ctx, raw); return; }

            JsonObject? body;
            try { body = JsonNode.Parse(raw) as JsonObject; }
            catch (JsonException) { body = null; }
            if (body is null || !Turns.IsAutoModel(body["model"], Turns.CodexAutoModel))
            {
                Debug($"/responses model={body?["model"]} -> passthrough");
                await Loopback.ForwardAsync(target, ctx, raw);
                return;
            }

            var key = Turns.CodexConversationKey(body);
            var prompt = Turns.CodexNewTurn(body);
            var decision = prompt is not null ? await router.DecideAsync(key, Turns.CodexFeatures(body), ctx.RequestAborted) : router.Current(key);
            if (decision is null)
            {
                var spec = router.SafeDefault();
                Turns.ApplyCodexModel(body, spec, spec.Effort.Contains("high") ? "high" : null);
                Debug($"/responses no decision yet -> safe default {spec.Model}");
            }
            else
            {
                Turns.ApplyCodexModel(body, decision.Model, decision.Effort);
                Debug($"/responses {(prompt is not null ? "NEW TURN" : "continuation")} -> {decision.Model.Model}{(decision.Effort is null ? "" : $" @{decision.Effort}")} ({decision.Judgment.Source}, task {decision.Task}) via {upstream}");
            }
            await Loopback.ForwardAsync(target, ctx, System.Text.Encoding.UTF8.GetBytes(body.ToJsonString()));
        });

        var userNamedModel = args.Any(a => a is "--model" or "-m" || a.StartsWith("--model=", StringComparison.Ordinal));
        var codexArgs = new List<string>();
        if (!userNamedModel) codexArgs.AddRange(["--model", Turns.CodexAutoModel]);
        codexArgs.AddRange([
            "--config", $"model_provider=\"{ProviderId}\"",
            "--config", $"model_providers.{ProviderId}.name=\"Dispatcher\"",
            "--config", $"model_providers.{ProviderId}.base_url=\"http://127.0.0.1:{server.Port}\"",
            "--config", $"model_providers.{ProviderId}.wire_api=\"responses\"",
            "--config", $"model_providers.{ProviderId}.requires_openai_auth=true",
            "--config", $"model_providers.{ProviderId}.supports_websockets=false",
        ]);
        codexArgs.AddRange(args);
        Console.Error.WriteLine($"[dispatcher] proxy on 127.0.0.1:{server.Port}; jev {(router.Judge.Enabled ? "on" : "OFF (no TYPESAFE_API_KEY; safe default only)")}; models: {string.Join(", ", router.Models.Select(m => m.Model))}");
        return await Launch.RunChildAsync(cli, codexArgs, new Dictionary<string, string>());
    }

    private static void Debug(string msg)
    {
        if (Env.Has("DISPATCHER_DEBUG")) Console.Error.WriteLine($"[dispatcher] {msg}");
    }

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None });

    /// <summary>Codex validates <c>--model</c> against the provider's catalog, so the Dispatcher row is added to the list.</summary>
    private static async Task ForwardModelsAsync(string target, HttpContext ctx)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, target);
        foreach (var (k, v) in ctx.Request.Headers)
        {
            if (k.Equals("host", StringComparison.OrdinalIgnoreCase) || k.Equals("content-length", StringComparison.OrdinalIgnoreCase) || k.Equals("accept-encoding", StringComparison.OrdinalIgnoreCase)) continue;
            req.Headers.TryAddWithoutValidation(k, (IEnumerable<string?>)v);
        }
        req.Headers.TryAddWithoutValidation("accept-encoding", "identity");
        using var upstream = await Http.SendAsync(req, ctx.RequestAborted);
        var text = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
        try
        {
            if (JsonNode.Parse(text) is JsonObject json && json["models"] is JsonArray models && !models.OfType<JsonObject>().Any(m => m["slug"]?.GetValue<string>() == Turns.CodexAutoModel))
            {
                var template = models.OfType<JsonObject>().FirstOrDefault(m => m["visibility"]?.GetValue<string>() == "list") ?? models.OfType<JsonObject>().FirstOrDefault();
                if (template is not null)
                {
                    var row = (JsonObject)template.DeepClone();
                    row["slug"] = Turns.CodexAutoModel;
                    row["display_name"] = "Dispatcher";
                    row["description"] = "Jev picks the cheapest model and effort for each turn.";
                    row["visibility"] = "list";
                    models.Insert(0, row);
                    text = json.ToJsonString();
                }
            }
        }
        catch (JsonException) { /* not the catalog shape; pass through */ }
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
        await ctx.Response.WriteAsync(text, ctx.RequestAborted);
    }
}
