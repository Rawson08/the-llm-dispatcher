import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { forward, startLoopback } from "./proxy-util.js";
import { childEnv, resolveOnPath, runChild } from "./launch.js";
import { CLAUDE_STATUS_FILE, STATUS_DIR, TurnRouter } from "./session.js";
import { CLAUDE_AUTO_MODEL, anthropicFeatures, anthropicNewTurn, applyClaudeModel, conversationKey, isAutoModel } from "./turns.js";

/**
 * `llm-dispatcher claude [claude args...]`
 *
 * Launches the real Claude Code with a loopback proxy as ANTHROPIC_BASE_URL and a "Dispatcher"
 * row in the /model picker. Claude Code keeps its own login, tools, permissions and sessions.
 * The proxy rewrites only the `model` and effort fields of requests that name the Dispatcher
 * row, forwards everything else byte-for-byte, and streams responses straight back.
 *
 * Anthropic documents this shape: with ANTHROPIC_BASE_URL set and no gateway credential, the
 * saved claude.ai login stays the active credential and its usage limits apply.
 */
export async function runClaude(args: string[]): Promise<number> {
  const cli = resolveOnPath("claude");
  if (!cli) {
    console.error("claude was not found on PATH. Install Claude Code: https://code.claude.com/docs/en/setup");
    return 1;
  }
  const upstream = (process.env.ANTHROPIC_BASE_URL ?? "https://api.anthropic.com").replace(/\/$/, "");
  const savedModel = readSavedModel();
  const router = new TurnRouter({
    provider: "anthropic",
    allowList: process.env.DISPATCHER_CLAUDE_MODELS,
    baselineModel: process.env.ANTHROPIC_MODEL ?? savedModel,
    statusFile: CLAUDE_STATUS_FILE,
  });

  const { port, close } = await startLoopback(async (req, res, raw) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    const target = `${upstream}${url.pathname}${url.search}`;
    const isMessages = req.method === "POST" && /^\/v1\/messages(\/count_tokens)?$/.test(url.pathname);
    if (!isMessages || raw.length === 0) return forward(target, req, raw, res);

    let body: Record<string, unknown>;
    try {
      body = JSON.parse(raw.toString("utf8"));
    } catch {
      return forward(target, req, raw, res);
    }
    if (!isAutoModel(body.model, CLAUDE_AUTO_MODEL)) {
      debug(`${url.pathname} model=${String(body.model)} -> passthrough (concrete model)`);
      return forward(target, req, raw, res); // user picked a concrete model
    }

    // Auxiliary calls (session titles, summaries) carry no tools; they never need a frontier model.
    if (!Array.isArray(body.tools) || body.tools.length === 0) {
      const cheap = router.cheapest();
      applyClaudeModel(body, cheap, undefined);
      debug(`${url.pathname} auxiliary (no tools) -> ${cheap.model}`);
      return forward(target, req, Buffer.from(JSON.stringify(body)), res);
    }

    const key = conversationKey(firstUserText(body));
    const prompt = anthropicNewTurn(body);
    const decision = prompt && url.pathname === "/v1/messages" ? await router.decide(key, anthropicFeatures(body)) : router.current(key);
    if (!decision) {
      const spec = router.safeDefault();
      applyClaudeModel(body, spec, spec.effort.includes("high") ? "high" : undefined);
      const msgs = Array.isArray(body.messages) ? (body.messages as Array<Record<string, unknown>>) : [];
      const last = msgs[msgs.length - 1];
      const shape = last ? `${String(last.role)}:${Array.isArray(last.content) ? (last.content as Array<{ type: string }>).map((b) => b.type).join("+") : typeof last.content}` : "none";
      debug(`${url.pathname} no decision yet -> safe default ${spec.model} (tools=${(body.tools as unknown[]).length} last=${shape})`);
    } else {
      applyClaudeModel(body, decision.model, decision.effort);
      debug(`${url.pathname} ${prompt ? "NEW TURN" : "continuation"} -> ${decision.model.model}${decision.effort ? ` @${decision.effort}` : ""} (${decision.judgment.source}, task ${decision.task})`);
    }
    return forward(target, req, Buffer.from(JSON.stringify(body)), res);
  });

  const env: Record<string, string> = {
    ANTHROPIC_BASE_URL: `http://127.0.0.1:${port}`,
    ANTHROPIC_CUSTOM_MODEL_OPTION: CLAUDE_AUTO_MODEL,
    ANTHROPIC_CUSTOM_MODEL_OPTION_NAME: "Dispatcher",
    ANTHROPIC_CUSTOM_MODEL_OPTION_DESCRIPTION: "Jev picks the cheapest Claude model and effort for each turn",
    ANTHROPIC_CUSTOM_MODEL_OPTION_SUPPORTED_CAPABILITIES: "thinking,adaptive_thinking,interleaved_thinking,effort,max_effort",
    CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT: "1",
  };
  const userNamedModel = args.some((a) => a === "--model" || a.startsWith("--model="));
  if (!process.env.ANTHROPIC_MODEL && !userNamedModel) env.ANTHROPIC_MODEL = CLAUDE_AUTO_MODEL;

  const extraArgs = statusLineArgs();
  console.error(
    `[dispatcher] proxy on 127.0.0.1:${port} -> ${upstream}; jev ${router.judge.enabled ? "on" : "OFF (no TYPESAFE_API_KEY; safe default only)"}; models: ${router.models.map((m) => m.model).join(", ")}`,
  );
  try {
    return await runChild(cli, [...extraArgs, ...args], childEnv(env));
  } finally {
    close();
    restoreSavedModel(savedModel);
  }
}

function debug(msg: string): void {
  if (process.env.DISPATCHER_DEBUG) process.stderr.write(`[dispatcher] ${msg}\n`);
}

function firstUserText(body: Record<string, unknown>): string {
  const messages = Array.isArray(body.messages) ? (body.messages as Array<Record<string, unknown>>) : [];
  const first = messages.find((m) => m.role === "user");
  const c = first?.content;
  if (typeof c === "string") return c.slice(0, 2000);
  if (Array.isArray(c)) return c.map((b) => (b?.type === "text" ? String(b.text ?? "") : "")).join("\n").slice(0, 2000);
  return "";
}

/* ---- Claude Code saves a picker choice made with Enter as the default; never leave our sentinel behind ---- */

const USER_SETTINGS = join(homedir(), ".claude", "settings.json");

export function readSavedModel(file = USER_SETTINGS): string | undefined {
  try {
    const model = JSON.parse(readFileSync(file, "utf8")).model;
    return model === CLAUDE_AUTO_MODEL ? undefined : model;
  } catch {
    return undefined;
  }
}

export function restoreSavedModel(previous: string | undefined, file = USER_SETTINGS): boolean {
  try {
    const settings = JSON.parse(readFileSync(file, "utf8"));
    if (settings.model !== CLAUDE_AUTO_MODEL) return false;
    if (previous === undefined) delete settings.model;
    else settings.model = previous;
    writeFileSync(file, `${JSON.stringify(settings, null, 2)}\n`);
    return true;
  } catch {
    return false;
  }
}

/**
 * Claude Code shows the model it asked for, not the one the proxy chose, so a status line is the
 * only place the decision can appear. A status line the user configured themselves wins.
 */
function statusLineArgs(): string[] {
  if (process.env.DISPATCHER_NO_STATUSLINE) return [];
  for (const dir of [join(process.cwd(), ".claude"), join(homedir(), ".claude")]) {
    try {
      if (JSON.parse(readFileSync(join(dir, "settings.json"), "utf8")).statusLine) return [];
    } catch {
      /* nothing to preserve */
    }
  }
  const script = process.argv[1] ?? "";
  const command = /\.ts$/.test(script)
    ? `npx tsx "${script}" statusline`
    : `"${process.execPath}" "${script}" statusline`;
  const file = join(STATUS_DIR, "claude-settings.json");
  try {
    mkdirSync(STATUS_DIR, { recursive: true });
    writeFileSync(file, JSON.stringify({ statusLine: { type: "command", command } }));
    return existsSync(file) ? ["--settings", file] : [];
  } catch {
    return [];
  }
}
