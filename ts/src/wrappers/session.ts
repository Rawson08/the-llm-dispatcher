import { mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { findModel, loadCatalog } from "../catalog.js";
import { Judge } from "../judge.js";
import { Ledger } from "../ledger.js";
import { decide, type PolicyOptions } from "../policy.js";
import type { Decision, Features, ModelSpec, ProviderName } from "../types.js";
import { summarize } from "../types.js";

export const STATUS_DIR = join(tmpdir(), "llm-dispatcher");
export const CLAUDE_STATUS_FILE = join(STATUS_DIR, "claude-status.json");
export const CODEX_STATUS_FILE = join(STATUS_DIR, "codex-status.json");

export interface TurnRouterOptions {
  /** Which provider's catalog entries the subscription can reach. */
  provider: ProviderName;
  /** Optional comma-separated allow-list of catalog ids or model strings (env override). */
  allowList?: string;
  /** Model the CLI would have used on its own; drives the savings estimate. */
  baselineModel?: string;
  statusFile?: string;
  policy?: PolicyOptions;
  judge?: Judge;
  ledgerPath?: string | null;
}

/**
 * Per-session routing state for a CLI wrapper. One Jev call per fresh user turn; tool-loop
 * continuations reuse the turn's decision so the model never changes mid-task.
 */
export class TurnRouter {
  readonly models: ModelSpec[];
  readonly judge: Judge;
  readonly ledger: Ledger;
  private readonly byConversation = new Map<string, Decision>();
  private last: Decision | undefined;
  private readonly baselineId?: string;

  constructor(private readonly o: TurnRouterOptions) {
    const all = loadCatalog().filter((m) => m.provider === o.provider && !/^openrouter\//.test(m.id));
    const allow = (o.allowList ?? "").split(",").map((s) => s.trim()).filter(Boolean);
    this.models = allow.length ? allow.map((a) => findModel(all, a)).filter((m): m is ModelSpec => !!m) : all;
    if (this.models.length === 0) throw new Error(`dispatcher: no ${o.provider} models in the catalog match ${o.allowList}`);
    this.judge = o.judge ?? new Judge();
    this.ledger = new Ledger(o.ledgerPath === undefined ? process.env.DISPATCHER_LEDGER_PATH ?? "dispatcher-ledger.jsonl" : o.ledgerPath ?? undefined);
    this.baselineId = o.baselineModel ? findModel(this.models, o.baselineModel.replace(/\[.*\]$/, ""))?.id : undefined;
  }

  /** Decide for a fresh turn and remember it under `key`. */
  async decide(key: string, features: Features): Promise<Decision> {
    const judgment = await this.judge.judge(features);
    const d = decide(features, judgment, this.models, { ...(this.o.policy ?? {}), baselineId: this.baselineId });
    this.byConversation.set(key, d);
    if (this.byConversation.size > 200) this.byConversation.delete(this.byConversation.keys().next().value!);
    this.last = d;
    this.ledger.record(d, { inputTokens: d.estimate.inputTokens, outputTokens: d.estimate.outputTokens, upstreamMs: 0, stream: true });
    this.writeStatus(d);
    return d;
  }

  /** The decision that governs a continuation of `key`, falling back to the most recent turn. */
  current(key: string): Decision | undefined {
    return this.byConversation.get(key) ?? this.last;
  }

  /** Conservative choice for requests that arrive before any turn has been routed. */
  safeDefault(): ModelSpec {
    return [...this.models].sort((a, b) => b.tier - a.tier || b.price.output - a.price.output)[0];
  }

  /** Cheapest model in the session's list, for auxiliary calls that never need capability. */
  cheapest(): ModelSpec {
    return [...this.models].sort((a, b) => a.price.output - b.price.output || a.tier - b.tier)[0];
  }

  private writeStatus(d: Decision): void {
    if (!this.o.statusFile) return;
    try {
      mkdirSync(STATUS_DIR, { recursive: true });
      writeFileSync(this.o.statusFile, JSON.stringify({ ...summarize(d), ts: new Date().toISOString() }));
    } catch {
      /* status is best-effort */
    }
  }
}
