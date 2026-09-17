import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import type { ModelSpec, ProviderConfig } from "./types.js";
import { EFFORT_LADDER, TASKS } from "./types.js";
import { ENV, has } from "./env.js";

const here = dirname(fileURLToPath(import.meta.url));

export interface CatalogFile {
  providers: Record<string, ProviderConfig>;
  models: ModelSpec[];
}

/** Providers the dispatcher knows without any catalog declaration. */
export const BUILTIN_PROVIDERS: Record<string, ProviderConfig> = {
  anthropic: { baseUrl: "https://api.anthropic.com", apiKeyEnv: ENV.anthropic },
  openai: { baseUrl: "https://api.openai.com/v1", apiKeyEnv: ENV.openai, useMaxCompletionTokens: true },
  openrouter: { baseUrl: "https://openrouter.ai/api/v1", apiKeyEnv: ENV.openrouter },
};

/** The shared catalog lives at the repo root; fall back to a package-local copy when published. */
function defaultCatalogPath(): string {
  const candidates = [
    resolve(process.cwd(), "models.json"),
    resolve(here, "..", "..", "models.json"),
    resolve(here, "..", "models.json"),
  ];
  return candidates.find((p) => existsSync(p)) ?? candidates[0];
}

/** Load models and custom providers from the default catalog or a user-supplied JSON file. */
export function loadCatalogFile(path?: string): CatalogFile {
  const file = path ?? process.env[ENV.catalog] ?? defaultCatalogPath();
  const raw = JSON.parse(readFileSync(file, "utf8")) as Partial<CatalogFile> | ModelSpec[];
  const models = Array.isArray(raw) ? raw : (raw.models ?? []);
  const providers = { ...BUILTIN_PROVIDERS, ...(Array.isArray(raw) ? {} : (raw.providers ?? {})) };
  for (const [name, p] of Object.entries(providers)) {
    if (!p.baseUrl) throw new Error(`catalog: provider "${name}" needs a baseUrl`);
  }
  for (const m of models) validate(m, providers);
  return { providers, models };
}

/** Convenience: just the models. */
export function loadCatalog(path?: string): ModelSpec[] {
  return loadCatalogFile(path).models;
}

function validate(m: ModelSpec, providers: Record<string, ProviderConfig>): void {
  const bad = (msg: string): never => {
    throw new Error(`catalog: model "${m?.id ?? "?"}": ${msg}`);
  };
  if (!m.id || !m.provider || !m.model) bad("id, provider and model are required");
  if (!providers[m.provider]) bad(`unknown provider "${m.provider}"; declare it under "providers"`);
  if (![1, 2, 3].includes(m.tier)) bad("tier must be 1, 2 or 3");
  if (!m.price || m.price.input < 0 || m.price.output < 0) bad("price.input/output required");
  for (const e of m.effort ?? []) if (!EFFORT_LADDER.includes(e)) bad(`unknown effort "${e}"`);
  for (const t of [...(m.strengths ?? []), ...(m.weaknesses ?? [])])
    if (!TASKS.includes(t)) bad(`unknown task "${t}"`);
}

/** A provider is usable when it is enabled and either needs no key or its key is present. */
export function providerAvailable(p: ProviderConfig | undefined): boolean {
  if (!p || p.enabled === false) return false;
  return !p.apiKeyEnv || has(p.apiKeyEnv);
}

/** Models whose provider is usable right now. */
export function availableModels(catalog: ModelSpec[], providers: Record<string, ProviderConfig> = BUILTIN_PROVIDERS): ModelSpec[] {
  return catalog.filter((m) => providerAvailable(providers[m.provider]));
}

export function findModel(catalog: ModelSpec[], name: string): ModelSpec | undefined {
  const n = name.toLowerCase();
  return catalog.find((m) => m.id.toLowerCase() === n || m.model.toLowerCase() === n);
}
