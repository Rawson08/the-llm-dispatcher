using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LlmDispatcher;
using LlmDispatcher.Providers;
using Microsoft.AspNetCore.Http.Json;

Env.Load();

var cmd = args.Length > 0 ? args[0] : "help";
var rest = args.Skip(1).ToArray();

switch (cmd)
{
    case "serve":
        Serve(rest);
        break;
    case "route":
        await RouteAsync(rest);
        break;
    case "models":
        Models();
        break;
    case "stats":
        Console.WriteLine(JsonSerializer.Serialize(new Dispatcher().Ledger.GetStats(), new JsonSerializerOptions(Json.Wire) { WriteIndented = true }));
        break;
    case "claude":
        Environment.ExitCode = await LlmDispatcher.Wrappers.ClaudeWrapper.RunAsync(rest);
        break;
    case "codex":
        Environment.ExitCode = await LlmDispatcher.Wrappers.CodexWrapper.RunAsync(rest);
        break;
    case "statusline":
        await LlmDispatcher.Wrappers.Statusline.RunAsync();
        break;
    default:
        Usage(cmd == "help" ? null : $"unknown command \"{cmd}\"");
        break;
}

/* ------------------------------------------------------------------ */

void Serve(string[] a)
{
    var portArg = Flag(a, "--port") ?? Env.Get(Env.Port) ?? "8787";
    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
    builder.WebHost.UseUrls($"http://localhost:{portArg}");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.Configure<JsonOptions>(o =>
    {
        o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });
    var app = builder.Build();
    var dispatcher = new Dispatcher();

    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers.AccessControlAllowOrigin = "*";
        ctx.Response.Headers.AccessControlAllowHeaders = "authorization, content-type, x-api-key";
        ctx.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
        if (ctx.Request.Method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }
        var required = Env.Get(Env.ApiKey);
        if (!string.IsNullOrEmpty(required))
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth != $"Bearer {required}" && ctx.Request.Headers["x-api-key"] != required)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(Error("unauthorized", "auth"));
                return;
            }
        }
        try { await next(); }
        catch (Exception ex)
        {
            if (ctx.Response.HasStarted)
            {
                await ctx.Response.WriteAsync($"data: {new JsonObject { ["error"] = new JsonObject { ["message"] = Redact(ex.Message) } }.ToJsonString()}\n\n");
                return;
            }
            var status = ex is UpstreamException { Status: >= 400 } ue ? ue.Status : 500;
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(Error(Redact(ex.Message), status == 500 ? "server_error" : "upstream_error"));
        }
    });

    app.MapGet("/health", () => Results.Json(new { ok = true, jev = dispatcher.Judge.Enabled }));
    app.MapGet("/stats", () => Results.Json(dispatcher.Ledger.GetStats(), Json.Wire));

    foreach (var path in new[] { "/v1/models", "/models" })
        app.MapGet(path, () =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var data = new List<object> { new { id = "the-llm-dispatcher/auto", @object = "model", created = now, owned_by = "dispatcher" } };
            data.AddRange(dispatcher.Available().Select(m => new { id = m.Id, @object = "model", created = now, owned_by = m.Provider }));
            return Results.Json(new { @object = "list", data });
        });

    foreach (var path in new[] { "/v1/route", "/route" })
        app.MapPost(path, async (HttpContext ctx) =>
        {
            var req = await ReadRequest(ctx) ?? throw new InvalidOperationException("invalid JSON body");
            var d = await dispatcher.RouteAsync(req, ctx.RequestAborted);
            var o = d.Summary();
            o["candidates"] = JsonSerializer.SerializeToNode(d.Candidates, Json.Wire);
            o["estimate"] = JsonSerializer.SerializeToNode(d.Estimate, Json.Wire);
            return Results.Text(o.ToJsonString(), "application/json");
        });

    foreach (var path in new[] { "/v1/chat/completions", "/chat/completions" })
        app.MapPost(path, async (HttpContext ctx) =>
        {
            var req = await ReadRequest(ctx) ?? throw new InvalidOperationException("invalid JSON body");
            if (req.Messages.Count == 0)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(Error("messages[] required", "invalid_request_error"));
                return;
            }
            if (string.IsNullOrEmpty(req.Model)) req.Model = "auto";
            var decision = await dispatcher.RouteAsync(req, ctx.RequestAborted);
            var summary = decision.Summary(includeRationale: false);
            ctx.Response.Headers["x-dispatcher-model"] = decision.Model.Id;
            if (decision.Effort is not null) ctx.Response.Headers["x-dispatcher-effort"] = decision.Effort;
            ctx.Response.Headers["x-dispatcher-decision"] = summary.ToJsonString();

            if (req.Stream == true)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                await foreach (var chunk in dispatcher.StreamAsync(req, decision, ctx.RequestAborted))
                {
                    await ctx.Response.WriteAsync($"data: {chunk.ToJsonString()}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted);
                return;
            }
            var (_, response) = await dispatcher.CompleteAsync(req, decision, ctx.RequestAborted);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(response.ToJsonString(), ctx.RequestAborted);
        });

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var avail = dispatcher.Available();
        Console.WriteLine($"the-llm-dispatcher listening on http://localhost:{portArg}");
        Console.WriteLine($"  jev routing : {(dispatcher.Judge.Enabled ? "enabled" : "DISABLED (no TYPESAFE_API_KEY; conservative fallback)")}");
        Console.WriteLine($"  providers   : {string.Join(", ", new[] { "anthropic", "openai", "openrouter" }.Where(p => avail.Any(m => m.Provider == p))) switch { "" => "none", var s => s }}");
        Console.WriteLine($"  models      : {avail.Count} available of {dispatcher.CatalogModels.Count} in catalog");
        Console.WriteLine($"  auth        : {(Env.Has(Env.ApiKey) ? "DISPATCHER_API_KEY required" : "open (set DISPATCHER_API_KEY to protect)")}");
    });
    app.Run();
}

async Task RouteAsync(string[] a)
{
    var text = string.Join(" ", a.Where(x => !x.StartsWith("--")));
    if (text.Length == 0) { Usage("route needs a prompt"); return; }
    var f = new Dispatcher(new DispatcherConfig { LedgerPath = null });
    if (f.Available().Count == 0)
    {
        Console.Error.WriteLine("(no provider keys configured; dry run against the full catalog)");
        f = new Dispatcher(new DispatcherConfig { LedgerPath = null, AssumeAllProviders = true });
    }
    var d = await f.RouteAsync(new ChatRequest { Model = "auto", Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement(text) }] });
    var s = d.Summary();
    if (a.Contains("--json"))
    {
        s["candidates"] = JsonSerializer.SerializeToNode(d.Candidates, Json.Wire);
        Console.WriteLine(s.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return;
    }
    Console.WriteLine($"model    : {d.Model.Id}{(d.Effort is null ? "" : $"  (effort: {d.Effort})")}");
    Console.WriteLine($"task     : {d.Task}  tier {d.Tier}  difficulty {s["difficulty"]}/4  reasoning {s["reasoning"]}/3  stakes {s["stakes"]}");
    Console.WriteLine($"source   : {d.Source}{(d.Judgment.Error is null ? "" : $"  ({d.Judgment.Error})")}  in {d.Judgment.LatencyMs} ms");
    Console.WriteLine($"est cost : ${d.Estimate.Cost:F6}  vs baseline {d.Estimate.BaselineModel} ${d.Estimate.BaselineCost:F6}  (saves ~${d.Estimate.Savings:F6})");
    Console.WriteLine("why      :");
    foreach (var r in d.Rationale) Console.WriteLine($"  - {r}");
    if (d.Candidates.Count > 0)
    {
        Console.WriteLine("ranked   :");
        foreach (var c in d.Candidates) Console.WriteLine($"  {c.Id,-34} tier {c.EffectiveTier}  ~${c.EstimatedCost:F6}");
    }
}

void Models()
{
    var f = new Dispatcher(new DispatcherConfig { LedgerPath = null });
    var avail = f.Available().Select(m => m.Id).ToHashSet();
    foreach (var m in f.CatalogModels)
        Console.WriteLine($"{(avail.Contains(m.Id) ? "✔" : "·")} {m.Id,-34} tier {m.Tier}  ${m.Price.Input}/${m.Price.Output} per M  {(m.Effort.Count > 0 ? $"effort {string.Join(",", m.Effort)}" : "no effort")}");
    Console.WriteLine("\n✔ = provider key present");
}

void Usage(string? msg)
{
    if (msg is not null) Console.Error.WriteLine($"error: {msg}\n");
    Console.WriteLine("""
        the-llm-dispatcher — an LLM router: Jev (TypeSafe System One) decides which model and how much reasoning effort each request deserves

          API proxy (needs provider API keys):
            llm-dispatcher serve [--port 8787]        start the OpenAI-compatible proxy
            llm-dispatcher route "<prompt>" [--json]  dry run: show the decision for a prompt
            llm-dispatcher models                     list the catalog and which providers are usable
            llm-dispatcher stats                      cost ledger totals

          CLI wrappers (use the CLI's own login, no API key; see README restrictions):
            llm-dispatcher claude [claude args...]    Claude Code with a Jev-chosen Claude model per turn
            llm-dispatcher codex  [codex args...]     Codex CLI with a Jev-chosen model per turn

        Keys are read from .env (TYPESAFE_API_KEY, ANTHROPIC_API_KEY, OPENAI_API_KEY, OPENROUTER_API_KEY).
        Local models: declare an OpenAI-compatible endpoint under "providers" in models.json.
        """);
    if (msg is not null) Environment.ExitCode = 2;
}

static string? Flag(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static async Task<ChatRequest?> ReadRequest(HttpContext ctx)
{
    try { return await JsonSerializer.DeserializeAsync<ChatRequest>(ctx.Request.Body, Json.Wire, ctx.RequestAborted); }
    catch (JsonException) { return null; }
}

static object Error(string message, string type) => new { error = new { message, type } };

/// <summary>Never echo secrets: strip anything that looks like an API key from error text.</summary>
static string Redact(string m) => Regex.Replace(m, @"(sk|key)[-_][A-Za-z0-9_-]{8,}", "[redacted]");
