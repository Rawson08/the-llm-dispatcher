using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmDispatcher;

/// <summary>
/// Asks Jev (TypeSafe's System One model) five independent questions about the request in one
/// HTTP call. There is no official C# TypeSafe SDK, so this speaks the documented HTTP API directly.
/// Jev sees the conversation text; dispatcher's code owns the policy.
/// </summary>
public sealed class Judge
{
    public const string Endpoint = "https://api.typesafe.ai/v1/systemone";
    public const string DefaultModel = "jev-latest";

    private readonly HttpClient? _http;
    private readonly string _model;
    public int TimeoutMs { get; }
    public bool Enabled => _http is not null;

    public Judge(HttpClient? http = null, int? timeoutMs = null, string? model = null, bool? enabled = null)
    {
        TimeoutMs = timeoutMs ?? (int.TryParse(Env.Get(Env.JevTimeout), out var t) ? t : 5000);
        _model = model ?? Env.Get("TYPESAFE_DEFAULT_MODEL") ?? DefaultModel;
        var on = enabled ?? Env.Has(Env.TypeSafe);
        if (!on) return;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMs * 2) };
    }

    public async Task<Judgment> JudgeAsync(Features f, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (_http is null) return Judgment.Fallback("no TYPESAFE_API_KEY configured", sw.ElapsedMilliseconds);

        try
        {
            var body = new JsonObject { ["state"] = BuildState(f), ["model"] = _model, ["questions"] = Questions() };
            var res = await SendAsync(body, ct);
            var answers = res.GetProperty("answers");
            var task = answers.GetProperty("task");
            var difficulty = answers.GetProperty("difficulty");
            var reasoning = answers.GetProperty("reasoning");
            var outputSize = answers.GetProperty("output_size");

            var probs = new Dictionary<string, double>();
            foreach (var p in task.GetProperty("probabilities").EnumerateObject()) probs[p.Name] = p.Value.GetDouble();

            return new Judgment
            {
                Task = task.GetProperty("choice").GetString() ?? "other",
                TaskConfidence = task.GetProperty("confidence").GetDouble(),
                TaskProbabilities = probs,
                Difficulty = new(difficulty.GetProperty("score").GetDouble(), difficulty.GetProperty("confidence").GetDouble()),
                Reasoning = new(reasoning.GetProperty("score").GetDouble(), reasoning.GetProperty("confidence").GetDouble()),
                Stakes = answers.GetProperty("stakes").GetProperty("noul").GetDouble(),
                OutputSize = new(outputSize.GetProperty("score").GetDouble(), outputSize.GetProperty("confidence").GetDouble()),
                Source = "jev",
                JevModel = res.TryGetProperty("model", out var m) ? m.GetString() : null,
                JevInputTokens = res.TryGetProperty("usage", out var u) && u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0,
                LatencyMs = sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Judgment.Fallback(ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>POST with one retry on 429/529, honouring Retry-After when present.</summary>
    private async Task<JsonElement> SendAsync(JsonObject body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeoutMs);
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Env.Get(Env.TypeSafe));
            using var res = await _http!.SendAsync(req, cts.Token);
            if ((res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode == 529) && attempt == 0)
            {
                var delay = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(400);
                await Task.Delay(delay > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : delay, ct);
                continue;
            }
            if (!res.IsSuccessStatusCode)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"typesafe {(int)res.StatusCode}: {text[..Math.Min(text.Length, 300)]}");
            }
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.Clone();
        }
    }

    private static JsonObject BuildState(Features f) => new()
    {
        ["conversation"] = new JsonObject
        {
            ["system_prompt"] = f.SystemExcerpt.Length > 0 ? f.SystemExcerpt : null,
            ["turns_so_far"] = f.Turns,
            ["recent_messages"] = new JsonArray(f.Recent.Select(t => (JsonNode)new JsonObject { ["role"] = t.Role, ["text"] = t.Text }).ToArray()),
            ["latest_user_message"] = f.LastUser,
        },
        ["request"] = new JsonObject
        {
            ["tools_available_to_assistant"] = new JsonArray(f.ToolNames.Select(n => (JsonNode)n!).ToArray()),
            ["includes_images"] = f.HasImages,
        },
    };

    /// <summary>Same questions as the TypeScript implementation; keep them in sync.</summary>
    public static JsonObject Questions() => new()
    {
        ["task"] = new JsonObject
        {
            ["type"] = "choice",
            ["instructions"] = new JsonObject
            {
                ["question"] = "What kind of work is the AI assistant being asked to do in `conversation.latest_user_message`, read in the context of the rest of `conversation`?",
                ["note"] = "Judge the work the model must perform, not the topic being discussed.",
            },
            ["criteria"] = new JsonObject
            {
                ["coding"] = "Write, modify, review, debug or explain source code, configuration, or shell commands",
                ["writing"] = "Draft or edit prose for a purpose: emails, posts, documentation, reports, cover letters",
                ["conversation"] = "Casual chat, greetings, quick opinions, small talk, or simple one-line factual questions",
                ["analysis"] = "Analyze, compare, plan, or reason about a situation, decision, design, or argument",
                ["math"] = "Mathematics, quantitative calculation, formal logic, or algorithmic problem solving",
                ["extraction"] = "Classify, label, or pull specific fields or structured data out of provided text",
                ["summarization"] = "Summarize, condense, or translate provided text without adding new content",
                ["creative"] = "Fiction, poetry, jokes, brainstorming, names, or other imaginative content",
                ["agentic"] = "Carry out a multi-step task using the available tools, such as browsing, running commands, or editing files",
                ["other"] = "None of the above fits well",
            },
        },
        ["difficulty"] = new JsonObject
        {
            ["type"] = "score",
            ["instructions"] = "How difficult is it for an AI language model to produce a fully correct, high-quality response to `conversation.latest_user_message` given `conversation`?",
            ["criteria"] = new JsonArray(
                "Trivial: a greeting, acknowledgement, one-line factual answer, or simple rewording; almost any model gets it right",
                "Routine: a common, well-specified task such as a short email, a small function, a plain explanation, or a basic summary",
                "Moderate: needs several steps or careful attention to detail, such as a multi-part document, a medium-sized code change, or a comparison with tradeoffs",
                "Hard: needs expert knowledge, subtle judgment, or long chains of dependent steps, such as debugging a tricky bug, designing a system, or a rigorous analysis",
                "Frontier: research-level or extremely intricate; even the strongest models frequently make mistakes"),
        },
        ["reasoning"] = new JsonObject
        {
            ["type"] = "score",
            ["instructions"] = "How much deliberate, step-by-step reasoning does a correct response require, as opposed to recall, rewording, or pattern completion?",
            ["criteria"] = new JsonArray(
                "None: recall, rewording, formatting, or casual conversation",
                "Light: a few obvious steps or simple lookups",
                "Substantial: careful multi-step reasoning where an early mistake propagates, such as non-trivial code logic or quantitative analysis",
                "Deep: extended deliberation, exploring alternatives, proofs, tricky debugging, or planning with many interacting constraints"),
        },
        ["stakes"] = new JsonObject
        {
            ["type"] = "noul",
            ["instructions"] = "Would a subtly wrong or low-quality response cause meaningful harm or cost? Consider medical, legal, financial, security, or production-code contexts, and cases where the user clearly signals the answer matters a great deal.",
        },
        ["output_size"] = new JsonObject
        {
            ["type"] = "score",
            ["instructions"] = "How long does a good response to `conversation.latest_user_message` need to be?",
            ["criteria"] = new JsonArray(
                "Short: a sentence to a paragraph",
                "Medium: several paragraphs, about a page, or a function-sized block of code",
                "Long: a multi-page document, a large code file, or many files"),
        },
    };
}
