import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import type { ModelSpec, ProviderName } from "./types.js";
import { EFFORT_LADDER, TASKS } from "./types.js";
import { ENV, has } from "./env.js";

const here = dirname(fileURLToPath(import.meta.url));

/** The shared catalog lives at the repo root; fall back to a package-local copy when published. */
function defaultCatalogPath(): string {
  const candidates = [
    resolve(process.cwd(), "models.json"),
    resolve(here, "..", "..", "models.json"),
    resolve(here, "..", "models.json"),
  ];
  return candidates.find((p) => existsSync(p)) ?? candidates[0];
}

/** Load the default catalog shipped with frugal, or a user-supplied JSON file. */
export function loadCatalog(path?: string): ModelSpec[] {
  const file = path ?? process.env[ENV.catalog] ?? defaultCatalogPath();
  const raw = JSON.parse(readFileSync(file, "utf8")) as { models?: ModelSpec[] } | ModelSpec[];
  const models = Array.isArray(raw) ? raw : (raw.models ?? []);
  for (const m of models) validate(m);
  return models;
}

function validate(m: ModelSpec): void {
  const bad = (msg: string): never => {
    throw new Error(`catalog: model "${m?.id ?? "?"}": ${msg}`);
  };
  if (!m.id || !m.provider || !m.model) bad("id, provider and model are required");
  if (![1, 2, 3].includes(m.tier)) bad("tier must be 1, 2 or 3");
  if (!m.price || m.price.input < 0 || m.price.output < 0) bad("price.input/output required");
  for (const e of m.effort ?? []) if (!EFFORT_LADDER.includes(e)) bad(`unknown effort "${e}"`);
  for (const t of [...(m.strengths ?? []), ...(m.weaknesses ?? [])])
    if (!TASKS.includes(t)) bad(`unknown task "${t}"`);
}

const PROVIDER_KEY: Record<ProviderName, string> = {
  anthropic: ENV.anthropic,
  openai: ENV.openai,
  openrouter: ENV.openrouter,
};

/** Models whose provider has credentials configured. */
export function availableModels(catalog: ModelSpec[]): ModelSpec[] {
  return catalog.filter((m) => has(PROVIDER_KEY[m.provider]));
}

export function findModel(catalog: ModelSpec[], name: string): ModelSpec | undefined {
  const n = name.toLowerCase();
  return catalog.find((m) => m.id.toLowerCase() === n || m.model.toLowerCase() === n);
}
