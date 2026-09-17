namespace Frugal;

public sealed class PolicyOptions
{
    /// <summary>Model the client would have used without frugal; defaults to the priciest frontier model.</summary>
    public string? BaselineId { get; set; }
    public double WeightDifficulty { get; set; } = 0.55;
    public double WeightReasoning { get; set; } = 0.30;
    public double WeightStakes { get; set; } = 0.15;
    /// <summary>Need score at which tier 2 starts.</summary>
    public double StandardThreshold { get; set; } = 0.28;
    /// <summary>Need score at which tier 3 starts.</summary>
    public double FrontierThreshold { get; set; } = 0.58;
    /// <summary>Below this difficulty confidence, bump one tier up for safety.</summary>
    public double MinConfidence { get; set; } = 0.45;
    /// <summary>Stakes probability above which economy models are excluded.</summary>
    public double HighStakes { get; set; } = 0.70;
}

/// <summary>
/// Pure policy: judgments + features + catalog -> decision. No I/O, so it is cheap to test and
/// cheap to re-run with new weights. Mirrors ts/src/policy.ts.
/// </summary>
public static class Policy
{
    /// <summary>Expected visible output tokens by output-size level (0, 1, 2).</summary>
    private static readonly int[] OutputTokens = [250, 1200, 5000];

    /// <summary>Output multiplier for reasoning tokens at each effort level.</summary>
    private static readonly Dictionary<string, double> EffortMultiplier = new()
    {
        ["none"] = 1, ["minimal"] = 1, ["low"] = 1.4, ["medium"] = 2.2, ["high"] = 3.5, ["xhigh"] = 5, ["max"] = 7,
    };

    public static Decision Decide(Features f, Judgment j, IReadOnlyList<ModelSpec> available, PolicyOptions? opts = null)
    {
        var o = opts ?? new PolicyOptions();
        var rationale = new List<string>();
        var task = j.Task;
        var d = j.Difficulty.Score;
        var r = j.Reasoning.Score;
        var s = j.Stakes;

        // 1. How much capability does this request need?
        var need = o.WeightDifficulty * (d / 4) + o.WeightReasoning * (r / 3) + o.WeightStakes * s;
        var tier = need < o.StandardThreshold ? 1 : need < o.FrontierThreshold ? 2 : 3;
        rationale.Add($"task={task} difficulty={d:F2} reasoning={r:F2} stakes={s:F2} need={need:F2} -> tier {tier}");

        if (j.Source == "fallback") rationale.Add($"jev unavailable ({j.Error ?? "unknown"}); using conservative defaults");
        if (s >= o.HighStakes && tier == 1)
        {
            tier = 2;
            rationale.Add($"stakes {s:F2} >= {o.HighStakes}: economy tier excluded");
        }
        if (j.Source == "jev" && j.Difficulty.Confidence < o.MinConfidence && tier < 3)
        {
            tier++;
            rationale.Add($"difficulty confidence {j.Difficulty.Confidence:F2} < {o.MinConfidence}: bumped to tier {tier}");
        }

        // 2. How hard should the chosen model think?
        var wantEffort = DesiredEffort(d, r, s);
        rationale.Add($"desired effort {wantEffort}");

        // 3. Filter candidates on hard requirements, then rank by expected cost.
        var outTokens = ExpectedOutputTokens(j.OutputSize.Score, f.RequestedMaxTokens);
        var inTokens = f.InputTokens;
        var scored = available
            .Where(m => MeetsRequirements(m, f, inTokens, outTokens))
            .Select(m => new Candidate(m.Id, EstimateCost(m, inTokens, outTokens, SnapEffort(wantEffort, m.Effort)), EffectiveTier(m, task)))
            .OrderBy(c => c.EstimatedCost)
            .ToList();

        var pool = scored.Where(c => c.EffectiveTier >= tier).ToList();
        if (pool.Count == 0)
        {
            var best = scored.Count > 0 ? scored.Max(c => c.EffectiveTier) : 0;
            pool = scored.Where(c => c.EffectiveTier == best).ToList();
            rationale.Add($"no eligible model at tier {tier}; using best available (tier {best})");
        }
        if (pool.Count == 0)
            throw new InvalidOperationException("frugal: no model satisfies the request (check provider keys, vision/tool support, context size)");

        var winner = pool[0];
        var chosen = available.First(m => m.Id == winner.Id);
        var effort = SnapEffort(wantEffort, chosen.Effort);
        if (EffectiveTier(chosen, task) > chosen.Tier) rationale.Add($"{chosen.Id} counts a tier higher for {task}");
        rationale.Add($"cheapest eligible: {chosen.Id}{(effort is null ? "" : $" @ {effort}")} (~${winner.EstimatedCost:F5})");

        // 4. What would the baseline have cost?
        var baseline = PickBaseline(available, o.BaselineId);
        var baselineCost = baseline is null
            ? winner.EstimatedCost
            : EstimateCost(baseline, inTokens, outTokens, SnapEffort("high", baseline.Effort));

        return new Decision
        {
            Model = chosen,
            Effort = effort,
            Tier = tier,
            Task = task,
            Judgment = j,
            Estimate = new Estimate
            {
                InputTokens = inTokens,
                OutputTokens = outTokens,
                Cost = winner.EstimatedCost,
                BaselineModel = baseline?.Id ?? chosen.Id,
                BaselineCost = baselineCost,
                Savings = Math.Max(0, baselineCost - winner.EstimatedCost),
            },
            Baseline = baseline,
            Rationale = rationale,
            Candidates = scored,
            Passthrough = false,
        };
    }

    public static string DesiredEffort(double difficulty, double reasoning, double stakes)
    {
        var e = reasoning < 0.5 ? "minimal" : reasoning < 1.25 ? "low" : reasoning < 2.0 ? "medium" : reasoning < 2.6 ? "high" : "xhigh";
        if (difficulty >= 3.2 && Efforts.Rank(e) < Efforts.Rank("high")) e = "high";
        if (reasoning >= 2.75 && stakes >= 0.7 && difficulty >= 3.5) e = "max";
        return e;
    }

    /// <summary>Snap a desired effort to the nearest level the model supports, preferring the higher neighbour.</summary>
    public static string? SnapEffort(string want, IReadOnlyList<string> supported)
    {
        if (supported.Count == 0) return null;
        if (supported.Contains(want)) return want;
        var w = Efforts.Rank(want);
        var sorted = supported.OrderBy(Efforts.Rank).ToList();
        return sorted.FirstOrDefault(e => Efforts.Rank(e) >= w) ?? sorted[^1];
    }

    public static int EffectiveTier(ModelSpec m, string task)
    {
        var t = m.Tier;
        if (m.Strengths?.Contains(task) == true) t++;
        if (m.Weaknesses?.Contains(task) == true) t--;
        return t;
    }

    public static double EstimateCost(ModelSpec m, int inTokens, int outTokens, string? effort)
    {
        var mult = effort is not null && EffortMultiplier.TryGetValue(effort, out var x) ? x : 1;
        return (inTokens * m.Price.Input + outTokens * mult * m.Price.Output) / 1e6;
    }

    public static int ExpectedOutputTokens(double outputSize, int? requestedMax)
    {
        var i = Math.Max(0, Math.Min(2, (int)Math.Floor(outputSize)));
        var frac = outputSize - i;
        var est = i == 2 ? OutputTokens[2] : OutputTokens[i] + frac * (OutputTokens[i + 1] - OutputTokens[i]);
        return (int)Math.Round(requestedMax is { } max ? Math.Min(est, max) : est);
    }

    private static bool MeetsRequirements(ModelSpec m, Features f, int inTokens, int outTokens)
    {
        if (f.HasImages && !m.Vision) return false;
        if (f.HasTools && !m.Tools) return false;
        if (inTokens * 1.1 + outTokens > m.Context) return false;
        if (f.RequestedMaxTokens is { } req && req > m.MaxOutput) return false;
        return true;
    }

    private static ModelSpec? PickBaseline(IReadOnlyList<ModelSpec> available, string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            var m = available.FirstOrDefault(x => x.Id == id || x.Model == id);
            if (m is not null) return m;
        }
        return available.OrderByDescending(m => m.Tier).ThenByDescending(m => m.Price.Output).FirstOrDefault();
    }
}
