import { spawn } from "node:child_process";
import { accessSync, constants } from "node:fs";
import { join } from "node:path";
import { loadedFromEnvFile } from "../env.js";

/** Provider credentials that would make a CLI bill an API key instead of its own login. */
const CREDENTIAL_VARS = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "OPENROUTER_API_KEY", "TYPESAFE_API_KEY"];

/**
 * Environment for the child CLI: the parent's, minus every credential the dispatcher itself
 * loaded from `.env`. A key the user exported in their own shell is left alone; that is their
 * explicit choice and the CLI will bill it.
 */
export function childEnv(extra: Record<string, string>): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { ...process.env };
  for (const k of CREDENTIAL_VARS) if (loadedFromEnvFile.includes(k)) delete env[k];
  return { ...env, ...extra };
}

export interface Resolved { file: string; prefix: string[]; shell: boolean }

/** Find a CLI on PATH, handling Windows shims (.cmd, .ps1, .exe). */
export function resolveOnPath(name: string): Resolved | null {
  const win = process.platform === "win32";
  const exts = win ? [".exe", ".cmd", ".bat", ".ps1", ""] : [""];
  for (const dir of (process.env.PATH ?? "").split(win ? ";" : ":")) {
    if (!dir) continue;
    for (const ext of exts) {
      const file = join(dir.replace(/^"|"$/g, ""), `${name}${ext}`);
      try {
        accessSync(file, constants.F_OK);
        if (/\.ps1$/i.test(file)) return { file: "powershell.exe", prefix: ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file], shell: false };
        return { file, prefix: [], shell: /\.(cmd|bat)$/i.test(file) };
      } catch {
        /* keep looking */
      }
    }
  }
  return null;
}

/**
 * Windows .cmd shims must run through cmd.exe, which joins arguments verbatim, so anything with
 * whitespace or shell metacharacters has to be quoted here or a prompt like "fix the bug"
 * arrives as three arguments.
 */
export function quoteForShell(arg: string): string {
  if (arg.length && !/[\s"&|<>^()%!]/.test(arg)) return arg;
  return `"${arg.replace(/(\\*)"/g, '$1$1\\"').replace(/%/g, "%%")}"`;
}

/** Run the CLI with inherited stdio and resolve with its exit code. */
export function runChild(cmd: Resolved, args: string[], env: NodeJS.ProcessEnv): Promise<number> {
  return new Promise((resolve) => {
    const all = [...cmd.prefix, ...args];
    const child = spawn(cmd.file, cmd.shell ? all.map(quoteForShell) : all, { stdio: "inherit", env, shell: cmd.shell, windowsHide: false });
    const relay = (sig: NodeJS.Signals) => () => child.kill(sig);
    process.on("SIGINT", relay("SIGINT"));
    process.on("SIGTERM", relay("SIGTERM"));
    child.on("exit", (code, signal) => resolve(code ?? (signal ? 130 : 1)));
    child.on("error", () => resolve(1));
  });
}
