using System.Text.Json;

namespace LlmDispatcher.Wrappers;

public sealed class TurnRouterOptions
{
    /// <summary>Which provider's catalog entries the subscription can reach.</summary>
    public required string Provider { get; init; }
    /// <summary>Optional comma-separated allow-list of catalog ids or model strings.</summary>
    public string? AllowList { get; init; }
    /// <summary>Model the CLI would have used on its own; drives the savings estimate.</summary>
    public string? BaselineModel { get; init; }
    public string? StatusFile { get; init; }
    public PolicyOptions? Policy { get; init; }
    public Judge? Judge { get; init; }
    /// <summary>Null disables the ledger; unset uses DISPATCHER_LEDGER_PATH or dispatcher-ledger.jsonl.</summary>
    public string? LedgerPath { get; init; } = "";
}

/// <summary>
/// Per-session routing state for a CLI wrapper. One Jev call per fresh user turn; tool-loop
/// continuations reuse the turn's decision so the model never changes mid-task.
/// </summary>
public sealed class TurnRouter
{
    public static readonly string StatusDir = Path.Combine(Path.GetTempPath(), "llm-dispatcher");
    public static readonly string ClaudeStatusFile = Path.Combine(StatusDir, "claude-status.json");
    public static readonly string CodexStatusFile = Path.Combine(StatusDir, "codex-status.json");

    public IReadOnlyList<ModelSpec> Models { get; }
    public Judge Judge { get; }
    public Ledger Ledger { get; }
    private readonly TurnRouterOptions _o;
    private readonly Dictionary<string, Decision> _byConversation = [];
    private readonly Queue<string> _order = new();
    private Decision? _last;
    private readonly string? _baselineId;
    private readonly object _lock = new();

    public TurnRouter(TurnRouterOptions o)
    {
        _o = o;
        var all = Catalog.Load().Where(m => m.Provider == o.Provider && !m.Id.StartsWith("openrouter/", StringComparison.Ordinal)).ToList();
        var allow = (o.AllowList ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Models = allow.Length > 0 ? allow.Select(a => Catalog.Find(all, a)).OfType<ModelSpec>().ToList() : all;
        if (Models.Count == 0) throw new InvalidOperationException($"dispatcher: no {o.Provider} models in the catalog match {o.AllowList}");
        Judge = o.Judge ?? new Judge();
        var ledgerPath = o.LedgerPath == "" ? Env.Get(Env.LedgerPath) ?? "dispatcher-ledger.jsonl" : o.LedgerPath;
        Ledger = new Ledger(ledgerPath);
        var baseline = o.BaselineModel is null ? null : System.Text.RegularExpressions.Regex.Replace(o.BaselineModel, @"\[.*\]$", "");
        _baselineId = baseline is null ? null : Catalog.Find(Models, baseline)?.Id;
    }

    /// <summary>Decide for a fresh turn and remember it under <paramref name="key"/>.</summary>
    public async Task<Decision> DecideAsync(string key, Features features, CancellationToken ct = default)
    {
        var judgment = await Judge.JudgeAsync(features, ct);
        var policy = _o.Policy ?? new PolicyOptions();
        policy.BaselineId = _baselineId;
        var d = Policy.Decide(features, judgment, Models, policy);
        lock (_lock)
        {
            if (!_byConversation.ContainsKey(key)) _order.Enqueue(key);
            _byConversation[key] = d;
            while (_order.Count > 200) _byConversation.Remove(_order.Dequeue());
            _last = d;
        }
        Ledger.Record(d, new ActualUsage { InputTokens = d.Estimate.InputTokens, OutputTokens = d.Estimate.OutputTokens, Stream = true });
        WriteStatus(d);
        return d;
    }

    /// <summary>The decision that governs a continuation of <paramref name="key"/>, falling back to the most recent turn.</summary>
    public Decision? Current(string key)
    {
        lock (_lock) return _byConversation.TryGetValue(key, out var d) ? d : _last;
    }

    /// <summary>Conservative choice for requests that arrive before any turn has been routed.</summary>
    public ModelSpec SafeDefault() => Models.OrderByDescending(m => m.Tier).ThenByDescending(m => m.Price.Output).First();

    /// <summary>Cheapest model in the session's list, for auxiliary calls that never need capability.</summary>
    public ModelSpec Cheapest() => Models.OrderBy(m => m.Price.Output).ThenBy(m => m.Tier).First();

    private void WriteStatus(Decision d)
    {
        if (_o.StatusFile is null) return;
        try
        {
            Directory.CreateDirectory(StatusDir);
            var o = d.Summary();
            o["ts"] = DateTimeOffset.UtcNow.ToString("o");
            File.WriteAllText(_o.StatusFile, o.ToJsonString());
        }
        catch (IOException) { /* status is best-effort */ }
    }
}
