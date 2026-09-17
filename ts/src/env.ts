/**
 * Loads `.env` from the working directory (or DISPATCHER_ENV_FILE) into process.env
 * without overriding variables that are already set. Values are never logged.
 */
import { existsSync } from "node:fs";
import { dirname, resolve } from "node:path";

/** Variables that loadEnv() itself added to process.env, so wrappers can keep them from child CLIs. */
export const loadedFromEnvFile: string[] = [];

/** Looks in the working directory, then up to three parents (the repo root when run from ts/). */
export function loadEnv(file = process.env.DISPATCHER_ENV_FILE ?? ".env"): boolean {
  let dir = process.cwd();
  for (let i = 0; i < 4; i++) {
    const path = resolve(dir, file);
    if (existsSync(path)) {
      try {
        const before = new Set(Object.keys(process.env));
        // Node 20.12+ / 22: parses the file and only sets variables not already defined.
        process.loadEnvFile(path);
        for (const k of Object.keys(process.env)) if (!before.has(k)) loadedFromEnvFile.push(k);
        return true;
      } catch {
        return false;
      }
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return false;
}

/** Env names dispatcher reads. Only presence is ever reported, never values. */
export const ENV = {
  typesafe: "TYPESAFE_API_KEY",
  anthropic: "ANTHROPIC_API_KEY",
  openai: "OPENAI_API_KEY",
  openaiBaseURL: "OPENAI_BASE_URL",
  openrouter: "OPENROUTER_API_KEY",
  port: "DISPATCHER_PORT",
  apiKey: "DISPATCHER_API_KEY",
  baseline: "DISPATCHER_BASELINE",
  fallback: "DISPATCHER_FALLBACK",
  routeAll: "DISPATCHER_ROUTE_ALL",
  ledger: "DISPATCHER_LEDGER_PATH",
  catalog: "DISPATCHER_CATALOG",
  jevTimeout: "DISPATCHER_JEV_TIMEOUT_MS",
} as const;

export function has(name: string): boolean {
  const v = process.env[name];
  return typeof v === "string" && v.trim().length > 0;
}
