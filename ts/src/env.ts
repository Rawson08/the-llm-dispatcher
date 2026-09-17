/**
 * Loads `.env` from the working directory (or FRUGAL_ENV_FILE) into process.env
 * without overriding variables that are already set. Values are never logged.
 */
import { existsSync } from "node:fs";
import { dirname, resolve } from "node:path";

/** Looks in the working directory, then up to three parents (the repo root when run from ts/). */
export function loadEnv(file = process.env.FRUGAL_ENV_FILE ?? ".env"): boolean {
  let dir = process.cwd();
  for (let i = 0; i < 4; i++) {
    const path = resolve(dir, file);
    if (existsSync(path)) {
      try {
        // Node 20.12+ / 22: parses the file and only sets variables not already defined.
        process.loadEnvFile(path);
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

/** Env names frugal reads. Only presence is ever reported, never values. */
export const ENV = {
  typesafe: "TYPESAFE_API_KEY",
  anthropic: "ANTHROPIC_API_KEY",
  openai: "OPENAI_API_KEY",
  openaiBaseURL: "OPENAI_BASE_URL",
  openrouter: "OPENROUTER_API_KEY",
  port: "FRUGAL_PORT",
  apiKey: "FRUGAL_API_KEY",
  baseline: "FRUGAL_BASELINE",
  fallback: "FRUGAL_FALLBACK",
  routeAll: "FRUGAL_ROUTE_ALL",
  ledger: "FRUGAL_LEDGER_PATH",
  catalog: "FRUGAL_CATALOG",
  jevTimeout: "FRUGAL_JEV_TIMEOUT_MS",
} as const;

export function has(name: string): boolean {
  const v = process.env[name];
  return typeof v === "string" && v.trim().length > 0;
}
