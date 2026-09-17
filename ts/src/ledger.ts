import { appendFileSync, existsSync, readFileSync } from "node:fs";
import type { Decision, Effort, ProviderName, Task } from "./types.js";
import { round } from "./types.js";

/** Jev price: USD per 1M input tokens (output is free). Verified 2026-09-17. */
export const JEV_PRICE_PER_M = 0.042;

export interface LedgerEntry {
  ts: string;
  model: string;
  provider: ProviderName;
  effort?: Effort;
  tier: number;
  task: Task;
  source: "jev" | "fallback" | "passthrough";
  inputTokens: number;
  outputTokens: number;
  costUsd: number;
  baselineModel: string;
  baselineCostUsd: number;
  jevInputTokens: number;
  jevCostUsd: number;
  decisionMs: number;
  upstreamMs: number;
  stream: boolean;
  error?: string;
}

export interface Stats {
  requests: number;
  routed: number;
  passthrough: number;
  jevFallbacks: number;
  errors: number;
  costUsd: number;
  baselineCostUsd: number;
  jevCostUsd: number;
  savedUsd: number;
  savedPct: number;
  byModel: Record<string, { requests: number; costUsd: number }>;
  byTask: Record<string, number>;
  byEffort: Record<string, number>;
}

/** Append-only JSONL ledger with in-memory aggregates. Stores no prompt text. */
export class Ledger {
  private entries: LedgerEntry[] = [];

  constructor(readonly path?: string) {
    if (path && existsSync(path)) {
      for (const line of readFileSync(path, "utf8").split("\n")) {
        if (!line.trim()) continue;
        try {
          this.entries.push(JSON.parse(line) as LedgerEntry);
        } catch {
          /* skip corrupt line */
        }
      }
    }
  }

  record(
    d: Decision,
    actual: { inputTokens: number; outputTokens: number; upstreamMs: number; stream: boolean; error?: string },
  ): LedgerEntry {
    const price = (m: { price: { input: number; output: number } }) =>
      (actual.inputTokens * m.price.input + actual.outputTokens * m.price.output) / 1e6;
    const baselineCost = d.baseline ? price(d.baseline) : d.estimate.baselineCost;

    const entry: LedgerEntry = {
      ts: new Date().toISOString(),
      model: d.model.id,
      provider: d.model.provider,
      effort: d.effort,
      tier: d.tier,
      task: d.task,
      source: d.passthrough ? "passthrough" : d.judgment.source,
      inputTokens: actual.inputTokens,
      outputTokens: actual.outputTokens,
      costUsd: round(price(d.model), 8),
      baselineModel: d.estimate.baselineModel,
      baselineCostUsd: round(baselineCost, 8),
      jevInputTokens: d.judgment.jevInputTokens,
      jevCostUsd: round((d.judgment.jevInputTokens * JEV_PRICE_PER_M) / 1e6, 8),
      decisionMs: d.judgment.latencyMs,
      upstreamMs: actual.upstreamMs,
      stream: actual.stream,
      error: actual.error,
    };
    this.entries.push(entry);
    if (this.path) {
      try {
        appendFileSync(this.path, JSON.stringify(entry) + "\n");
      } catch {
        /* ledger is best-effort */
      }
    }
    return entry;
  }

  stats(): Stats {
    const s: Stats = {
      requests: 0, routed: 0, passthrough: 0, jevFallbacks: 0, errors: 0,
      costUsd: 0, baselineCostUsd: 0, jevCostUsd: 0, savedUsd: 0, savedPct: 0,
      byModel: {}, byTask: {}, byEffort: {},
    };
    for (const e of this.entries) {
      s.requests++;
      if (e.source === "passthrough") s.passthrough++;
      else s.routed++;
      if (e.source === "fallback") s.jevFallbacks++;
      if (e.error) s.errors++;
      s.costUsd += e.costUsd;
      s.baselineCostUsd += e.baselineCostUsd;
      s.jevCostUsd += e.jevCostUsd;
      const bm = (s.byModel[e.model] ??= { requests: 0, costUsd: 0 });
      bm.requests++;
      bm.costUsd = round(bm.costUsd + e.costUsd, 8);
      s.byTask[e.task] = (s.byTask[e.task] ?? 0) + 1;
      const ef = e.effort ?? "n/a";
      s.byEffort[ef] = (s.byEffort[ef] ?? 0) + 1;
    }
    s.savedUsd = s.baselineCostUsd - s.costUsd - s.jevCostUsd;
    s.savedPct = s.baselineCostUsd > 0 ? (s.savedUsd / s.baselineCostUsd) * 100 : 0;
    for (const k of ["costUsd", "baselineCostUsd", "jevCostUsd", "savedUsd"] as const) s[k] = round(s[k], 6);
    s.savedPct = round(s.savedPct, 1);
    return s;
  }
}
