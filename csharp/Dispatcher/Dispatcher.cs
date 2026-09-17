using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using LlmDispatcher.Providers;

namespace LlmDispatcher;

public sealed class DispatcherConfig
{
    public List<ModelSpec>? Catalog { get; set; }
    /// <summary>Provider endpoints; defaults to the built-ins plus whatever the catalog file declares.</summary>
    public Dictionary<string, ProviderConfig>? ProviderConfigs { get; set; }
    public Judge? Judge { get; set; }
    /// <summary>Null disables the ledger file; unset uses DISPATCHER_LEDGER_PATH or dispatcher-ledger.jsonl.</summary>
    public string? LedgerPath { get; set; } = "";
    /// <summary>Model id that represents "what you would have used anyway".</summary>
    public string? BaselineId { get; set; }
    /// <summary>Model id to use when Jev is unavailable. Defaults to the policy's conservative choice.</summary>
    public string? FallbackId { get; set; }
    /// <summary>Route every request, even ones naming a concrete model.</summary>
    public bool? RouteAll { get; set; }
    /// <summary>Model names that mean "let dispatcher decide".</summary>
    public string[] Aliases { get; set; } = ["auto", "dispatcher", "the-llm-dispatcher/auto"];
    public PolicyOptions Policy { get; set; } = new();
    public Dictionary<string, IProvider>? Providers { get; set; }
    /// <summary>Dry-run mode: treat every catalog model as available even without provider keys.</summary>
    public bool AssumeAllProviders { get; set; }
}

/// <summary>Routes each request to a model and effort, calls the provider, and records the outcome.</summary>
public sealed class Dispatcher
{
    public IReadOnlyList<ModelSpec> CatalogModels { get; }
    public Dictionary<string, ProviderConfig> ProviderConfigs { get; }
    public Judge Judge { get; }
    public Ledger Ledger { get; }
    private readonly DispatcherConfig _cfg;
    private readonly HashSet<string> _aliases;
    private readonly Dictionary<string, IProvider> _providers;

    public Dispatcher(DispatcherConfig? cfg = null)
    {
        _cfg = cfg ?? new DispatcherConfig();
        var file = _cfg.Catalog is null || _cfg.ProviderConfigs is null ? Catalog.LoadFile() : null;
        CatalogModels = _cfg.Catalog ?? file!.Models;
        ProviderConfigs = _cfg.ProviderConfigs ?? file!.Providers;
        Judge = _cfg.Judge ?? new Judge();
        var ledgerPath = _cfg.LedgerPath == "" ? Env.Get(Env.LedgerPath) ?? "dispatcher-ledger.jsonl" : _cfg.LedgerPath;
        Ledger = new Ledger(ledgerPath);
        _aliases = new HashSet<string>(_cfg.Aliases.Select(a => a.ToLowerInvariant()));
        _providers = new Dictionary<string, IProvider>
        {
            ["anthropic"] = _cfg.Providers?.GetValueOrDefault("anthropic") ?? new AnthropicProvider(),
            ["openai"] = _cfg.Providers?.GetValueOrDefault("openai") ?? OpenAICompatibleProvider.OpenAI(),
            ["openrouter"] = _cfg.Providers?.GetValueOrDefault("openrouter") ?? OpenAICompatibleProvider.OpenRouter(),
        };
        foreach (var (name, pc) in ProviderConfigs)
            if (!_providers.ContainsKey(name)) _providers[name] = _cfg.Providers?.GetValueOrDefault(name) ?? OpenAICompatibleProvider.Custom(name, pc);
    }

    public List<ModelSpec> Available() => _cfg.AssumeAllProviders ? CatalogModels.ToList() : Catalog.Available(CatalogModels, ProviderConfigs);

    private IProvider ProviderFor(ModelSpec spec) =>
        _providers.TryGetValue(spec.Provider, out var p) ? p : throw new InvalidOperationException($"dispatcher: model {spec.Id} names provider \"{spec.Provider}\" which is not configured");

    public bool ShouldRoute(string? model) =>
        (_cfg.RouteAll ?? Env.Get(Env.RouteAll) == "1") || _aliases.Contains((model ?? "").ToLowerInvariant());

    /// <summary>Decide which model and effort should serve this request. Makes at most one Jev call.</summary>
    public async Task<Decision> RouteAsync(ChatRequest req, CancellationToken ct = default)
    {
        var features = FeatureExtractor.Extract(req);
        var available = Available();
        if (available.Count == 0)
            throw new InvalidOperationException("dispatcher: no usable provider (set ANTHROPIC_API_KEY, OPENAI_API_KEY or OPENROUTER_API_KEY, or enable a provider in models.json)");
        var ropts = req.DispatcherOptions ?? new RequestOptions();

        if (!ShouldRoute(req.Model))
        {
            var spec = Catalog.Find(available, req.Model)
                ?? throw new InvalidOperationException($"dispatcher: unknown model \"{req.Model}\". Use \"auto\" or one of: {string.Join(", ", available.Select(m => m.Id))}");
            return Passthrough(spec, features.InputTokens, req);
        }

        var judgment = await Judge.JudgeAsync(features, ct);
        var policy = Clone(_cfg.Policy);
        policy.BaselineId = ropts.Baseline ?? _cfg.BaselineId ?? Env.Get(Env.Baseline);
        var decision = Policy.Decide(features, judgment, available, policy);
        decision = ClampTier(decision, features, judgment, available, ropts, policy);

        var fallbackId = _cfg.FallbackId ?? Env.Get(Env.Fallback);
        if (judgment.Source == "fallback" && fallbackId is not null && Catalog.Find(available, fallbackId) is { } fb)
        {
            decision.Model = fb;
            decision.Effort = Policy.SnapEffort("high", fb.Effort);
            decision.Tier = fb.Tier;
            decision.Estimate.Cost = Policy.EstimateCost(fb, decision.Estimate.InputTokens, decision.Estimate.OutputTokens, decision.Effort);
            decision.Estimate.Savings = Math.Max(0, decision.Estimate.BaselineCost - decision.Estimate.Cost);
            decision.Rationale.Add($"DISPATCHER_FALLBACK -> {fb.Id}");
        }
        return decision;
    }

    private static Decision Passthrough(ModelSpec spec, int inputTokens, ChatRequest req)
    {
        var effort = Efforts.IsValid(req.ReasoningEffort) ? Policy.SnapEffort(req.ReasoningEffort!, spec.Effort) : null;
        var outTokens = Policy.ExpectedOutputTokens(1, req.RequestedMaxTokens);
        var cost = Policy.EstimateCost(spec, inputTokens, outTokens, effort);
        return new Decision
        {
            Model = spec,
            Effort = effort,
            Tier = spec.Tier,
            Task = "other",
            Judgment = Judgment.Fallback("passthrough"),
            Estimate = new Estimate { InputTokens = inputTokens, OutputTokens = outTokens, Cost = cost, BaselineModel = spec.Id, BaselineCost = cost, Savings = 0 },
            Baseline = spec,
            Rationale = [$"client requested {spec.Id} explicitly; not routed"],
            Passthrough = true,
        };
    }

    /// <summary>Route, call the chosen provider, and record the outcome.</summary>
    public async Task<(Decision Decision, JsonObject Response)> CompleteAsync(ChatRequest req, Decision? decision = null, CancellationToken ct = default)
    {
        var d = decision ?? await RouteAsync(req, ct);
        var provider = ProviderFor(d.Model);
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await provider.CompleteAsync(req, d.Model, d.Effort, ct);
            response["dispatcher"] = d.Summary();
            var usage = response["usage"] as JsonObject;
            Ledger.Record(d, new ActualUsage
            {
                InputTokens = (int?)usage?["prompt_tokens"] ?? d.Estimate.InputTokens,
                OutputTokens = (int?)usage?["completion_tokens"] ?? 0,
                UpstreamMs = sw.ElapsedMilliseconds,
                Stream = false,
            });
            return (d, response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Ledger.Record(d, new ActualUsage { InputTokens = d.Estimate.InputTokens, UpstreamMs = sw.ElapsedMilliseconds, Error = Clip(ex.Message) });
            throw;
        }
    }

    /// <summary>Route and stream chunks; the first chunk carries the decision, the last carries usage.</summary>
    public async IAsyncEnumerable<JsonObject> StreamAsync(ChatRequest req, Decision? decision = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var d = decision ?? await RouteAsync(req, ct);
        var provider = ProviderFor(d.Model);
        var sw = Stopwatch.StartNew();
        JsonObject? usage = null;
        var first = true;
        await using var e = provider.StreamAsync(req, d.Model, d.Effort, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool more;
            try { more = await e.MoveNextAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Ledger.Record(d, new ActualUsage { InputTokens = d.Estimate.InputTokens, UpstreamMs = sw.ElapsedMilliseconds, Stream = true, Error = Clip(ex.Message) });
                throw;
            }
            if (!more) break;
            var chunk = e.Current;
            if (first) { chunk["dispatcher"] = d.Summary(); first = false; }
            if (chunk["usage"] is JsonObject u) usage = u;
            yield return chunk;
        }
        Ledger.Record(d, new ActualUsage
        {
            InputTokens = (int?)usage?["prompt_tokens"] ?? d.Estimate.InputTokens,
            OutputTokens = (int?)usage?["completion_tokens"] ?? d.Estimate.OutputTokens,
            UpstreamMs = sw.ElapsedMilliseconds,
            Stream = true,
        });
    }

    private static Decision ClampTier(Decision decision, Features f, Judgment j, List<ModelSpec> available, RequestOptions ropts, PolicyOptions policy)
    {
        var min = ropts.MinTier ?? 1;
        var max = ropts.MaxTier ?? 3;
        if (decision.Tier >= min && decision.Tier <= max) return decision;
        var pool = available.Where(m => m.Tier >= min && m.Tier <= max).ToList();
        var d2 = Policy.Decide(f, j, pool.Count > 0 ? pool : available, policy);
        d2.Tier = Math.Clamp(decision.Tier, min, max);
        d2.Rationale.Add($"client clamped tier to [{min}, {max}]");
        return d2;
    }

    private static PolicyOptions Clone(PolicyOptions p) => new()
    {
        BaselineId = p.BaselineId, WeightDifficulty = p.WeightDifficulty, WeightReasoning = p.WeightReasoning, WeightStakes = p.WeightStakes,
        StandardThreshold = p.StandardThreshold, FrontierThreshold = p.FrontierThreshold, MinConfidence = p.MinConfidence, HighStakes = p.HighStakes,
    };

    private static string Clip(string s) => s.Length > 200 ? s[..200] : s;
}
