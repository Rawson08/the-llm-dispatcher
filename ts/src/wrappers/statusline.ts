import { readFileSync } from "node:fs";
import { basename } from "node:path";
import { CLAUDE_STATUS_FILE } from "./session.js";

/**
 * `llm-dispatcher statusline`: Claude Code runs this and pipes session JSON on stdin.
 * Prints the last routing decision so the user can see which model actually served the turn.
 */
export async function statusline(): Promise<void> {
  let input: Record<string, unknown> = {};
  try {
    const raw = readFileSync(0, "utf8");
    if (raw.trim()) input = JSON.parse(raw);
  } catch {
    /* no stdin */
  }
  const cwd = (input.workspace as Record<string, string> | undefined)?.current_dir ?? (input.cwd as string | undefined) ?? process.cwd();
  const pct = (input.context_window as Record<string, number> | undefined)?.used_percentage;
  const requested = (input.model as Record<string, string> | undefined)?.display_name ?? "";
  const parts: string[] = [];

  let status: Record<string, unknown> | undefined;
  try {
    status = JSON.parse(readFileSync(CLAUDE_STATUS_FILE, "utf8"));
  } catch {
    /* no decision yet */
  }
  if (requested && requested !== "Dispatcher" && !/dispatcher/i.test(requested)) {
    parts.push(`⏸ manual ${requested}`);
  } else if (status) {
    const model = String(status.model ?? "").replace(/^anthropic\//, "");
    const effort = status.effort ? ` @${status.effort}` : "";
    const conf = (status.confidence as Record<string, number> | undefined)?.difficulty;
    const src = status.source === "jev" ? `jev${conf !== undefined ? ` p=${conf.toFixed(2)}` : ""}` : String(status.source ?? "");
    parts.push(`⚡ ${model}${effort} (${status.task ?? "?"}, ${src})`);
  } else {
    parts.push("⚡ dispatcher: waiting for first turn");
  }
  parts.push(basename(cwd));
  if (typeof pct === "number") parts.push(`${Math.round(pct)}% context`);
  process.stdout.write(parts.join(" · "));
}
