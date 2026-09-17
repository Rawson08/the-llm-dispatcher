using System.Text.Json;

namespace LlmDispatcher;

public sealed class LedgerEntry
{
    public string Ts { get; set; } = "";
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? Effort { get; set; }
    public int Tier { get; set; }
    public string Task { get; set; } = "";
    public string Source { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double CostUsd { get; set; }
    public string BaselineModel { get; set; } = "";
    public double BaselineCostUsd { get; set; }
    public int JevInputTokens { get; set; }
    public double JevCostUsd { get; set; }
    public long DecisionMs { get; set; }
    public long UpstreamMs { get; set; }
    public bool Stream { get; set; }
    public string? Error { get; set; }
}

public sealed class ActualUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public long UpstreamMs { get; init; }
    public bool Stream { get; init; }
    public string? Error { get; init; }
}

public sealed class Stats
{
    public int Requests { get; set; }
    public int Routed { get; set; }
    public int Passthrough { get; set; }
    public int JevFallbacks { get; set; }
    public int Errors { get; set; }
    public double CostUsd { get; set; }
    public double BaselineCostUsd { get; set; }
    public double JevCostUsd { get; set; }
    public double SavedUsd { get; set; }
    public double SavedPct { get; set; }
    public Dictionary<string, ModelStats> ByModel { get; set; } = [];
    public Dictionary<string, int> ByTask { get; set; } = [];
    public Dictionary<string, int> ByEffort { get; set; } = [];
}

public sealed class ModelStats
{
    public int Requests { get; set; }
    public double CostUsd { get; set; }
}

/// <summary>Append-only JSONL ledger with in-memory aggregates. Stores no prompt text.</summary>
public sealed class Ledger
{
    /// <summary>Jev price: USD per 1M input tokens (output is free). Verified 2026-09-17.</summary>
    public const double JevPricePerM = 0.042;

    private readonly List<LedgerEntry> _entries = [];
    private readonly object _lock = new();
    public string? Path { get; }

    public Ledger(string? path)
    {
        Path = path;
        if (path is null || !File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<LedgerEntry>(line, Json.Camel);
                if (e is not null) _entries.Add(e);
            }
            catch (JsonException) { /* skip corrupt line */ }
        }
    }

    public LedgerEntry Record(Decision d, ActualUsage actual)
    {
        double PriceAt(ModelSpec m) => (actual.InputTokens * m.Price.Input + actual.OutputTokens * m.Price.Output) / 1e6;
        var entry = new LedgerEntry
        {
            Ts = DateTimeOffset.UtcNow.ToString("o"),
            Model = d.Model.Id,
            Provider = d.Model.Provider,
            Effort = d.Effort,
            Tier = d.Tier,
            Task = d.Task,
            Source = d.Source,
            InputTokens = actual.InputTokens,
            OutputTokens = actual.OutputTokens,
            CostUsd = Math.Round(PriceAt(d.Model), 8),
            BaselineModel = d.Estimate.BaselineModel,
            BaselineCostUsd = Math.Round(d.Baseline is null ? d.Estimate.BaselineCost : PriceAt(d.Baseline), 8),
            JevInputTokens = d.Judgment.JevInputTokens,
            JevCostUsd = Math.Round(d.Judgment.JevInputTokens * JevPricePerM / 1e6, 8),
            DecisionMs = d.Judgment.LatencyMs,
            UpstreamMs = actual.UpstreamMs,
            Stream = actual.Stream,
            Error = actual.Error,
        };
        lock (_lock)
        {
            _entries.Add(entry);
            if (Path is not null)
            {
                try { File.AppendAllText(Path, JsonSerializer.Serialize(entry, Json.Camel) + "\n"); }
                catch (IOException) { /* ledger is best-effort */ }
            }
        }
        return entry;
    }

    public Stats GetStats()
    {
        var s = new Stats();
        lock (_lock)
        {
            foreach (var e in _entries)
            {
                s.Requests++;
                if (e.Source == "passthrough") s.Passthrough++; else s.Routed++;
                if (e.Source == "fallback") s.JevFallbacks++;
                if (e.Error is not null) s.Errors++;
                s.CostUsd += e.CostUsd;
                s.BaselineCostUsd += e.BaselineCostUsd;
                s.JevCostUsd += e.JevCostUsd;
                if (!s.ByModel.TryGetValue(e.Model, out var bm)) s.ByModel[e.Model] = bm = new ModelStats();
                bm.Requests++;
                bm.CostUsd = Math.Round(bm.CostUsd + e.CostUsd, 8);
                s.ByTask[e.Task] = s.ByTask.GetValueOrDefault(e.Task) + 1;
                var ef = e.Effort ?? "n/a";
                s.ByEffort[ef] = s.ByEffort.GetValueOrDefault(ef) + 1;
            }
        }
        s.SavedUsd = s.BaselineCostUsd - s.CostUsd - s.JevCostUsd;
        s.SavedPct = s.BaselineCostUsd > 0 ? s.SavedUsd / s.BaselineCostUsd * 100 : 0;
        s.CostUsd = Math.Round(s.CostUsd, 6);
        s.BaselineCostUsd = Math.Round(s.BaselineCostUsd, 6);
        s.JevCostUsd = Math.Round(s.JevCostUsd, 6);
        s.SavedUsd = Math.Round(s.SavedUsd, 6);
        s.SavedPct = Math.Round(s.SavedPct, 1);
        return s;
    }
}
