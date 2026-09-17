import { availableModels, findModel, loadCatalog } from "./catalog.js";
import { ENV } from "./env.js";
import { extractFeatures } from "./features.js";
import { Judge, fallback } from "./judge.js";
import { Ledger } from "./ledger.js";
import { decide, estimateCost, expectedOutputTokens, snapEffort, type PolicyOptions } from "./policy.js";
import { AnthropicProvider } from "./providers/anthropic.js";
import { openaiProvider, openrouterProvider } from "./providers/openai-compatible.js";
import type { ChatChunk, Provider } from "./providers/types.js";
import type { ChatRequest, ChatResponse, Decision, Effort, ModelSpec, ProviderName, Tier } from "./types.js";
import { EFFORT_LADDER, summarize } from "./types.js";

export interface DispatcherConfig {
  catalog?: ModelSpec[];
  judge?: Judge;
  ledgerPath?: string | null;
  /** Model id that represents "what you would have used anyway". */
  baselineId?: string;
  /** Model id to use when Jev is unavailable. Defaults to the policy's conservative choice. */
  fallbackId?: string;
  /** Route every request, even ones naming a concrete model. */
  routeAll?: boolean;
  /** Model names that mean "let dispatcher decide". */
  aliases?: string[];
  policy?: PolicyOptions;
  providers?: Partial<Record<ProviderName, Provider>>;
  /** Dry-run mode: treat every catalog model as available even without provider keys. */
  assumeAllProviders?: boolean;
}

/** Per-request knobs a client may send in the body under `dispatcher_options`. */
export interface RequestOptions {
  baseline?: string;
  min_tier?: Tier;
  max_tier?: Tier;
}

export class Dispatcher {
  readonly catalog: ModelSpec[];
  readonly judge: Judge;
  readonly ledger: Ledger;
  readonly aliases: Set<string>;
  private readonly cfg: DispatcherConfig;
  private readonly providers: Record<ProviderName, Provider>;

  constructor(cfg: DispatcherConfig = {}) {
    this.cfg = cfg;
    this.catalog = cfg.catalog ?? loadCatalog();
    this.judge = cfg.judge ?? new Judge();
    const ledgerPath = cfg.ledgerPath === undefined ? (process.env[ENV.ledger] ?? "dispatcher-ledger.jsonl") : cfg.ledgerPath;
    this.ledger = new Ledger(ledgerPath ?? undefined);
    this.aliases = new Set((cfg.aliases ?? ["auto", "dispatcher", "the-llm-dispatcher/auto"]).map((a) => a.toLowerCase()));
    this.providers = {
      anthropic: cfg.providers?.anthropic ?? new AnthropicProvider(),
      openai: cfg.providers?.openai ?? openaiProvider(),
      openrouter: cfg.providers?.openrouter ?? openrouterProvider(),
    };
  }

  available(): ModelSpec[] {
    return this.cfg.assumeAllProviders ? this.catalog : availableModels(this.catalog);
  }

  shouldRoute(model: string): boolean {
    if (this.cfg.routeAll ?? process.env[ENV.routeAll] === "1") return true;
    return this.aliases.has((model ?? "").toLowerCase());
  }

  /** Decide which model and effort should serve this request. Makes at most one Jev call. */
  async route(req: ChatRequest): Promise<Decision> {
    const features = extractFeatures(req);
    const available = this.available();
    if (available.length === 0) throw new Error("dispatcher: no provider keys configured (ANTHROPIC_API_KEY, OPENAI_API_KEY or OPENROUTER_API_KEY)");
    const ropts = (req.dispatcher_options ?? {}) as RequestOptions;

    if (!this.shouldRoute(req.model)) {
      const spec = findModel(available, req.model);
      if (!spec) throw new Error(`dispatcher: unknown model "${req.model}". Use "auto" or one of: ${available.map((m) => m.id).join(", ")}`);
      return this.passthrough(spec, features.inputTokens, req);
    }

    const judgment = await this.judge.judge(features);
    const baselineId = ropts.baseline ?? this.cfg.baselineId ?? process.env[ENV.baseline];
    let decision = decide(features, judgment, available, { ...(this.cfg.policy ?? {}), baselineId });

    decision = clampTier(decision, features, judgment, available, ropts, { ...(this.cfg.policy ?? {}), baselineId });

    const fallbackId = this.cfg.fallbackId ?? process.env[ENV.fallback];
    if (judgment.source === "fallback" && fallbackId) {
      const spec = findModel(available, fallbackId);
      if (spec) {
        decision.model = spec;
        decision.effort = snapEffort("high", spec.effort);
        decision.tier = spec.tier;
        decision.estimate.cost = estimateCost(spec, decision.estimate.inputTokens, decision.estimate.outputTokens, decision.effort);
        decision.estimate.savings = Math.max(0, decision.estimate.baselineCost - decision.estimate.cost);
        decision.rationale.push(`DISPATCHER_FALLBACK -> ${spec.id}`);
      }
    }
    return decision;
  }

  private passthrough(spec: ModelSpec, inputTokens: number, req: ChatRequest): Decision {
    const j = fallback("passthrough");
    const effort = req.reasoning_effort && EFFORT_LADDER.includes(req.reasoning_effort as Effort)
      ? snapEffort(req.reasoning_effort as Effort, spec.effort)
      : undefined;
    const outTokens = expectedOutputTokens(1, req.max_completion_tokens ?? req.max_tokens);
    const cost = estimateCost(spec, inputTokens, outTokens, effort);
    return {
      model: spec,
      effort,
      tier: spec.tier,
      task: "other",
      judgment: j,
      estimate: { inputTokens, outputTokens: outTokens, cost, baselineModel: spec.id, baselineCost: cost, savings: 0 },
      baseline: spec,
      rationale: [`client requested ${spec.id} explicitly; not routed`],
      candidates: [],
      passthrough: true,
    };
  }

  /** Route, call the chosen provider, and record the outcome. */
  async complete(req: ChatRequest, decision?: Decision): Promise<{ decision: Decision; response: ChatResponse }> {
    const d = decision ?? (await this.route(req));
    const provider = this.providers[d.model.provider];
    const started = Date.now();
    try {
      const response = await provider.complete(req, d.model, d.effort);
      response.dispatcher = summarize(d);
      this.ledger.record(d, {
        inputTokens: response.usage?.prompt_tokens ?? d.estimate.inputTokens,
        outputTokens: response.usage?.completion_tokens ?? 0,
        upstreamMs: Date.now() - started,
        stream: false,
      });
      return { decision: d, response };
    } catch (err) {
      this.ledger.record(d, {
        inputTokens: d.estimate.inputTokens,
        outputTokens: 0,
        upstreamMs: Date.now() - started,
        stream: false,
        error: err instanceof Error ? err.message.slice(0, 200) : String(err),
      });
      throw err;
    }
  }

  /** Route and stream chunks; the first chunk carries the decision, the last carries usage. */
  async *stream(req: ChatRequest, decision?: Decision): AsyncGenerator<ChatChunk, void, undefined> {
    const d = decision ?? (await this.route(req));
    const provider = this.providers[d.model.provider];
    const started = Date.now();
    let usage: ChatChunk["usage"] | undefined;
    let first = true;
    try {
      for await (const chunk of provider.stream(req, d.model, d.effort)) {
        if (first) {
          chunk.dispatcher = summarize(d);
          first = false;
        }
        if (chunk.usage) usage = chunk.usage;
        yield chunk;
      }
      this.ledger.record(d, {
        inputTokens: usage?.prompt_tokens ?? d.estimate.inputTokens,
        outputTokens: usage?.completion_tokens ?? d.estimate.outputTokens,
        upstreamMs: Date.now() - started,
        stream: true,
      });
    } catch (err) {
      this.ledger.record(d, {
        inputTokens: d.estimate.inputTokens,
        outputTokens: 0,
        upstreamMs: Date.now() - started,
        stream: true,
        error: err instanceof Error ? err.message.slice(0, 200) : String(err),
      });
      throw err;
    }
  }
}

function clampTier(
  decision: Decision,
  features: ReturnType<typeof extractFeatures>,
  judgment: Decision["judgment"],
  available: ModelSpec[],
  ropts: RequestOptions,
  policy: PolicyOptions,
): Decision {
  const min = ropts.min_tier ?? 1;
  const max = ropts.max_tier ?? 3;
  if (decision.tier >= min && decision.tier <= max) return decision;
  const target = Math.min(max, Math.max(min, decision.tier)) as Tier;
  // Re-run the policy against only the models at the permitted tiers.
  const pool = available.filter((m) => m.tier >= min && m.tier <= max);
  const d2 = decide(features, judgment, pool.length ? pool : available, policy);
  d2.tier = target;
  d2.rationale.push(`client clamped tier to [${min}, ${max}]`);
  return d2;
}
