/** Shared types for dispatcher. Policy and catalog are plain data so they stay editable. */

/**
 * "anthropic", "openai" and "openrouter" are built in. Any other name must be declared in the
 * catalog's `providers` section as an OpenAI-compatible endpoint (Ollama, LM Studio, vLLM, ...).
 */
export type ProviderName = string;

/** An OpenAI-compatible endpoint declared in the catalog. */
export interface ProviderConfig {
  baseUrl: string;
  /** Env var holding the bearer key; null or absent means the endpoint needs no key. */
  apiKeyEnv?: string | null;
  /** Set false to keep a keyless provider (a local server) out of routing until you opt in. */
  enabled?: boolean;
  /** Extra headers to send with every request. */
  headers?: Record<string, string>;
  /** OpenAI's newer models reject `max_tokens`; set true to rename it. */
  useMaxCompletionTokens?: boolean;
}

/** Canonical effort ladder, lowest to highest. Providers support subsets. */
export const EFFORT_LADDER = ["none", "minimal", "low", "medium", "high", "xhigh", "max"] as const;
export type Effort = (typeof EFFORT_LADDER)[number];

export const TASKS = [
  "coding",
  "writing",
  "conversation",
  "analysis",
  "math",
  "extraction",
  "summarization",
  "creative",
  "agentic",
  "other",
] as const;
export type Task = (typeof TASKS)[number];

export type Tier = 1 | 2 | 3;

export interface ModelSpec {
  /** Stable id clients may request directly, e.g. "anthropic/claude-sonnet-5". */
  id: string;
  provider: ProviderName;
  /** Model string sent to the provider. */
  model: string;
  name: string;
  /** Your belief about general quality: 1 economy, 2 standard, 3 frontier. */
  tier: Tier;
  /** USD per 1M tokens. */
  price: { input: number; output: number };
  context: number;
  maxOutput: number;
  vision: boolean;
  tools: boolean;
  /** Effort levels the provider accepts for this model; empty means none. */
  effort: Effort[];
  /** Tasks where this model performs one tier above its listed tier. */
  strengths?: Task[];
  /** Tasks where this model performs one tier below its listed tier. */
  weaknesses?: Task[];
}

/* ---------- OpenAI-compatible chat wire format (our proxy's public surface) ---------- */

export interface ContentPartText { type: "text"; text: string }
export interface ContentPartImage { type: "image_url"; image_url: { url: string; detail?: string } }
export type ContentPart = ContentPartText | ContentPartImage | { type: string; [k: string]: unknown };

export interface ToolCall {
  id: string;
  type: "function";
  function: { name: string; arguments: string };
}

export interface ChatMessage {
  role: "system" | "developer" | "user" | "assistant" | "tool";
  content: string | ContentPart[] | null;
  name?: string;
  tool_calls?: ToolCall[];
  tool_call_id?: string;
}

export interface ToolDef {
  type: "function";
  function: { name: string; description?: string; parameters?: Record<string, unknown>; strict?: boolean };
}

export interface ChatRequest {
  model: string;
  messages: ChatMessage[];
  tools?: ToolDef[];
  tool_choice?: "auto" | "none" | "required" | { type: "function"; function: { name: string } };
  max_tokens?: number;
  max_completion_tokens?: number;
  temperature?: number;
  top_p?: number;
  stream?: boolean;
  stream_options?: { include_usage?: boolean };
  reasoning_effort?: string;
  stop?: string | string[];
  response_format?: unknown;
  user?: string;
  [k: string]: unknown;
}

export interface ChatUsage { prompt_tokens: number; completion_tokens: number; total_tokens: number }

export interface ChatResponse {
  id: string;
  object: "chat.completion";
  created: number;
  model: string;
  choices: Array<{
    index: number;
    message: { role: "assistant"; content: string | null; tool_calls?: ToolCall[] };
    finish_reason: "stop" | "length" | "tool_calls" | "content_filter" | null;
  }>;
  usage: ChatUsage;
  dispatcher?: DecisionSummary;
}

/* ---------- Routing ---------- */

export interface Features {
  inputTokens: number;
  turns: number;
  hasImages: boolean;
  hasTools: boolean;
  toolNames: string[];
  requestedMaxTokens?: number;
  systemExcerpt: string;
  recent: Array<{ role: string; text: string }>;
  lastUser: string;
}

export interface Judgment {
  task: { choice: Task; confidence: number; probabilities: Record<string, number> };
  /** 0..4 */
  difficulty: { score: number; confidence: number };
  /** 0..3 */
  reasoning: { score: number; confidence: number };
  /** probability that a wrong answer is costly, 0..1 */
  stakes: number;
  /** 0..2 */
  outputSize: { score: number; confidence: number };
  source: "jev" | "fallback";
  jevModel?: string;
  jevInputTokens: number;
  latencyMs: number;
  error?: string;
}

export interface Candidate { id: string; estimatedCost: number; effectiveTier: number }

export interface Decision {
  model: ModelSpec;
  effort?: Effort;
  tier: Tier;
  task: Task;
  judgment: Judgment;
  estimate: {
    inputTokens: number;
    outputTokens: number;
    cost: number;
    baselineModel: string;
    baselineCost: number;
    savings: number;
  };
  /** Baseline spec, kept so the ledger can price actual usage at baseline rates. */
  baseline?: ModelSpec;
  rationale: string[];
  candidates: Candidate[];
  /** True when the client named a concrete model and no routing happened. */
  passthrough: boolean;
}

/** Compact form attached to responses and headers. Never contains prompt text. */
export interface DecisionSummary {
  model: string;
  provider: ProviderName;
  effort?: Effort;
  tier: Tier;
  task: Task;
  difficulty: number;
  reasoning: number;
  stakes: number;
  confidence: { task: number; difficulty: number };
  source: "jev" | "fallback" | "passthrough";
  estimated_cost_usd: number;
  baseline_model: string;
  baseline_cost_usd: number;
  estimated_savings_usd: number;
  rationale: string[];
}

export function summarize(d: Decision): DecisionSummary {
  return {
    model: d.model.id,
    provider: d.model.provider,
    effort: d.effort,
    tier: d.tier,
    task: d.task,
    difficulty: round(d.judgment.difficulty.score, 2),
    reasoning: round(d.judgment.reasoning.score, 2),
    stakes: round(d.judgment.stakes, 2),
    confidence: { task: round(d.judgment.task.confidence, 2), difficulty: round(d.judgment.difficulty.confidence, 2) },
    source: d.passthrough ? "passthrough" : d.judgment.source,
    estimated_cost_usd: round(d.estimate.cost, 6),
    baseline_model: d.estimate.baselineModel,
    baseline_cost_usd: round(d.estimate.baselineCost, 6),
    estimated_savings_usd: round(d.estimate.savings, 6),
    rationale: d.rationale,
  };
}

export function round(n: number, places: number): number {
  const f = 10 ** places;
  return Math.round(n * f) / f;
}
