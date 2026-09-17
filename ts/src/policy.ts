import type { Candidate, Decision, Effort, Features, Judgment, ModelSpec, Task, Tier } from "./types.js";
import { EFFORT_LADDER } from "./types.js";

/**
 * Pure policy: judgments + features + catalog -> decision.
 * No I/O, so it is cheap to test and cheap to re-run with new weights.
 */
export interface PolicyOptions {
  /** Model the client would have used without frugal; defaults to the priciest frontier model. */
  baselineId?: string;
  /** Weights for the capability-need score. */
  weights?: { difficulty: number; reasoning: number; stakes: number };
  /** Need thresholds for tier 2 and tier 3. */
  tierThresholds?: { standard: number; frontier: number };
  /** Below this difficulty confidence, bump one tier up for safety. */
  minConfidence?: number;
  /** Stakes probability above which economy models are excluded. */
  highStakes?: number;
}

const DEFAULTS: Required<PolicyOptions> = {
  baselineId: "",
  weights: { difficulty: 0.55, reasoning: 0.3, stakes: 0.15 },
  tierThresholds: { standard: 0.28, frontier: 0.58 },
  minConfidence: 0.45,
  highStakes: 0.7,
};

/** Expected visible output tokens by output-size level (0, 1, 2). */
const OUTPUT_TOKENS = [250, 1200, 5000];
/** Output multiplier for reasoning tokens at each effort level. */
const EFFORT_MULTIPLIER: Record<Effort, number> = {
  none: 1, minimal: 1, low: 1.4, medium: 2.2, high: 3.5, xhigh: 5, max: 7,
};

export function decide(
  features: Features,
  judgment: Judgment,
  available: ModelSpec[],
  opts: PolicyOptions = {},
): Decision {
  const o = { ...DEFAULTS, ...opts };
  const rationale: string[] = [];
  const task = judgment.task.choice;
  const d = judgment.difficulty.score;
  const r = judgment.reasoning.score;
  const s = judgment.stakes;

  // 1. How much capability does this request need?
  const need = o.weights.difficulty * (d / 4) + o.weights.reasoning * (r / 3) + o.weights.stakes * s;
  let tier: Tier = need < o.tierThresholds.standard ? 1 : need < o.tierThresholds.frontier ? 2 : 3;
  rationale.push(
    `task=${task} difficulty=${d.toFixed(2)} reasoning=${r.toFixed(2)} stakes=${s.toFixed(2)} need=${need.toFixed(2)} -> tier ${tier}`,
  );

  if (judgment.source === "fallback") {
    rationale.push(`jev unavailable (${judgment.error ?? "unknown"}); using conservative defaults`);
  }
  if (s >= o.highStakes && tier === 1) {
    tier = 2;
    rationale.push(`stakes ${s.toFixed(2)} >= ${o.highStakes}: economy tier excluded`);
  }
  if (judgment.source === "jev" && judgment.difficulty.confidence < o.minConfidence && tier < 3) {
    tier = (tier + 1) as Tier;
    rationale.push(
      `difficulty confidence ${judgment.difficulty.confidence.toFixed(2)} < ${o.minConfidence}: bumped to tier ${tier}`,
    );
  }

  // 2. How hard should the chosen model think?
  const wantEffort = desiredEffort(d, r, s);
  rationale.push(`desired effort ${wantEffort}`);

  // 3. Filter candidates on hard requirements, then rank by expected cost.
  const outTokens = expectedOutputTokens(judgment.outputSize.score, features.requestedMaxTokens);
  const inTokens = features.inputTokens;
  const eligible = available.filter((m) => meetsRequirements(m, features, inTokens, outTokens));
  const scored: Candidate[] = eligible.map((m) => ({
    id: m.id,
    effectiveTier: effectiveTier(m, task),
    estimatedCost: estimateCost(m, inTokens, outTokens, snapEffort(wantEffort, m.effort)),
  }));

  let pool = scored.filter((c) => c.effectiveTier >= tier);
  if (pool.length === 0) {
    // Nothing at the required tier: take the most capable thing that fits.
    const best = Math.max(...scored.map((c) => c.effectiveTier), 0);
    pool = scored.filter((c) => c.effectiveTier === best);
    rationale.push(`no eligible model at tier ${tier}; using best available (tier ${best})`);
  }
  if (pool.length === 0) {
    throw new Error("frugal: no model satisfies the request (check provider keys, vision/tool support, context size)");
  }

  pool.sort((a, b) => a.estimatedCost - b.estimatedCost);
  const chosen = available.find((m) => m.id === pool[0].id)!;
  const effort = snapEffort(wantEffort, chosen.effort);
  if (effectiveTier(chosen, task) > chosen.tier) rationale.push(`${chosen.id} counts a tier higher for ${task}`);
  rationale.push(`cheapest eligible: ${chosen.id}${effort ? ` @ ${effort}` : ""} (~$${pool[0].estimatedCost.toFixed(5)})`);

  // 4. What would the baseline have cost?
  const baseline = pickBaseline(available, o.baselineId);
  const baselineCost = baseline
    ? estimateCost(baseline, inTokens, outTokens, snapEffort("high", baseline.effort))
    : pool[0].estimatedCost;

  return {
    model: chosen,
    effort,
    tier,
    task,
    judgment,
    estimate: {
      inputTokens: inTokens,
      outputTokens: outTokens,
      cost: pool[0].estimatedCost,
      baselineModel: baseline?.id ?? chosen.id,
      baselineCost,
      savings: Math.max(0, baselineCost - pool[0].estimatedCost),
    },
    baseline,
    rationale,
    candidates: scored.sort((a, b) => a.estimatedCost - b.estimatedCost),
    passthrough: false,
  };
}

export function desiredEffort(difficulty: number, reasoning: number, stakes: number): Effort {
  let e: Effort =
    reasoning < 0.5 ? "minimal" : reasoning < 1.25 ? "low" : reasoning < 2.0 ? "medium" : reasoning < 2.6 ? "high" : "xhigh";
  if (difficulty >= 3.2 && rank(e) < rank("high")) e = "high";
  if (reasoning >= 2.75 && stakes >= 0.7 && difficulty >= 3.5) e = "max";
  return e;
}

/** Snap a desired effort to the nearest level the model supports, preferring the higher neighbour. */
export function snapEffort(want: Effort, supported: Effort[]): Effort | undefined {
  if (!supported.length) return undefined;
  if (supported.includes(want)) return want;
  const w = rank(want);
  const sorted = [...supported].sort((a, b) => rank(a) - rank(b));
  return sorted.find((e) => rank(e) >= w) ?? sorted[sorted.length - 1];
}

export function rank(e: Effort): number {
  return EFFORT_LADDER.indexOf(e);
}

export function effectiveTier(m: ModelSpec, task: Task): number {
  let t: number = m.tier;
  if (m.strengths?.includes(task)) t += 1;
  if (m.weaknesses?.includes(task)) t -= 1;
  return t;
}

export function estimateCost(m: ModelSpec, inTokens: number, outTokens: number, effort?: Effort): number {
  const mult = effort ? EFFORT_MULTIPLIER[effort] : 1;
  return (inTokens * m.price.input + outTokens * mult * m.price.output) / 1e6;
}

export function expectedOutputTokens(outputSize: number, requestedMax?: number): number {
  const i = Math.max(0, Math.min(2, Math.floor(outputSize)));
  const frac = outputSize - i;
  const est = i === 2 ? OUTPUT_TOKENS[2] : OUTPUT_TOKENS[i] + frac * (OUTPUT_TOKENS[i + 1] - OUTPUT_TOKENS[i]);
  return Math.round(requestedMax ? Math.min(est, requestedMax) : est);
}

function meetsRequirements(m: ModelSpec, f: Features, inTokens: number, outTokens: number): boolean {
  if (f.hasImages && !m.vision) return false;
  if (f.hasTools && !m.tools) return false;
  if (inTokens * 1.1 + outTokens > m.context) return false;
  if (f.requestedMaxTokens && f.requestedMaxTokens > m.maxOutput) return false;
  return true;
}

function pickBaseline(available: ModelSpec[], id?: string): ModelSpec | undefined {
  if (id) {
    const m = available.find((x) => x.id === id || x.model === id);
    if (m) return m;
  }
  return [...available].sort((a, b) => b.tier - a.tier || b.price.output - a.price.output)[0];
}
