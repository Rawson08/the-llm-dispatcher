import { createHash } from "node:crypto";
import { truncate } from "../features.js";
import type { Effort, Features, ModelSpec } from "../types.js";

/**
 * Pure functions that understand what the two CLIs send. They decide whether a request opens a
 * fresh user turn, build routing features from it, and rewrite it for the chosen model.
 * Nothing here performs I/O, so all of it is unit-tested.
 */

export const CLAUDE_AUTO_MODEL = "dispatcher-auto";
export const CODEX_AUTO_MODEL = "dispatcher-auto";

type Json = Record<string, unknown>;

/** Claude Code and Codex inject bookkeeping blocks into user text; they blunt Jev's read of the prompt. */
export function cleanPrompt(text: string): string {
  return text
    .replace(/<system[-_]reminder>[\s\S]*?<\/system[-_]reminder>/gi, "")
    .replace(/<current_datetime>[\s\S]*?<\/current_datetime>/gi, "")
    .trim();
}

export function conversationKey(...parts: Array<string | undefined>): string {
  return createHash("sha1").update(parts.map((p) => p ?? "").join("|")).digest("hex").slice(0, 12);
}

/* ------------------------------ Anthropic Messages (Claude Code) ------------------------------ */

function anthropicText(content: unknown): string {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content
    .map((b) => (b && typeof b === "object" && (b as Json).type === "text" ? String((b as Json).text ?? "") : ""))
    .filter(Boolean)
    .join("\n");
}

function anthropicHasImage(content: unknown): boolean {
  return Array.isArray(content) && content.some((b) => b && typeof b === "object" && (b as Json).type === "image");
}

/** True when the model field asks the dispatcher to choose (with or without Claude Code's `[1m]` suffix). */
export function isAutoModel(model: unknown, auto: string): boolean {
  return typeof model === "string" && (model === auto || model.startsWith(`${auto}[`));
}

/**
 * The cleaned text of a genuinely new user turn, or null. A turn continues across many requests
 * while the agent runs tools; those end in `tool_result` and must reuse the turn's decision rather
 * than re-asking Jev and letting the model flip mid-task. Requests without tools are auxiliary
 * (titles, summaries) and are left alone.
 */
export function anthropicNewTurn(body: Json): string | null {
  const tools = body.tools;
  if (!Array.isArray(tools) || tools.length === 0) return null;
  const messages = body.messages;
  if (!Array.isArray(messages) || messages.length === 0) return null;
  // Claude Code appends operator `system` messages (effort switches, reminders) after the user turn.
  const last = [...(messages as Json[])].reverse().find((m) => m?.role !== "system");
  if (last?.role !== "user") return null;
  if (Array.isArray(last.content) && last.content.some((b) => (b as Json)?.type === "tool_result")) return null;
  const text = cleanPrompt(anthropicText(last.content));
  return text || null;
}

export function anthropicFeatures(body: Json): Features {
  const messages = (Array.isArray(body.messages) ? body.messages : []) as Json[];
  const system = Array.isArray(body.system)
    ? (body.system as Json[])
        .map((b) => String(b.text ?? ""))
        // Claude Code's first system block is a version/fingerprint attribution line, not instructions.
        .filter((t, i) => !(i === 0 && /^x-anthropic-billing|^claude-code/i.test(t)))
        .join("\n")
    : typeof body.system === "string"
      ? body.system
      : "";
  const turns = messages
    .filter((m) => m.role !== "system")
    .map((m) => ({ role: String(m.role), text: cleanPrompt(anthropicText(m.content)) }))
    .filter((t) => t.text);
  const lastUser = [...turns].reverse().find((t) => t.role === "user")?.text ?? "";
  const tools = (Array.isArray(body.tools) ? body.tools : []) as Json[];
  return {
    inputTokens: Math.ceil(JSON.stringify(body).length / 4),
    turns: messages.length,
    hasImages: messages.some((m) => anthropicHasImage(m.content)),
    hasTools: tools.length > 0,
    toolNames: tools.map((t) => String(t.name ?? "")).filter(Boolean).slice(0, 40),
    requestedMaxTokens: typeof body.max_tokens === "number" ? body.max_tokens : undefined,
    systemExcerpt: truncate(system, 1500),
    recent: turns.slice(-6).map((t) => ({ role: t.role, text: truncate(t.text, 1500) })),
    lastUser: truncate(lastUser, 8000),
  };
}

/** Anthropic effort levels; anything else the policy wanted is snapped by the caller. */
const ANTHROPIC_EFFORT = new Set<Effort>(["low", "medium", "high", "xhigh", "max"]);

/**
 * Point a Claude Code request at `spec`, removing fields that model cannot accept. Claude Code
 * composes the body for the model it believes it is talking to, so routing down to Haiku while
 * leaving adaptive thinking in place would be a hard 400.
 */
/**
 * Mid-conversation `system` messages (operator instructions and per-message effort changes) are
 * accepted by Opus 5, Opus 4.8 and the Fable / Mythos models only. Anything else returns a 400.
 */
export function supportsMidConversationSystem(model: string): boolean {
  return /opus-5|opus-4-8|fable|mythos/.test(model);
}

export function applyClaudeModel(body: Json, spec: ModelSpec, effort: Effort | undefined): Json {
  body.model = spec.model;
  const supportsEffort = spec.effort.length > 0;
  const wantEffort = supportsEffort && effort && ANTHROPIC_EFFORT.has(effort) && spec.effort.includes(effort) ? effort : undefined;

  if (Array.isArray(body.messages)) {
    const messages = body.messages as Json[];
    if (supportsMidConversationSystem(spec.model)) {
      // Keep operator messages but make any per-message effort agree with the routing decision.
      for (const m of messages) {
        if (m?.role !== "system") continue;
        const oc = m.output_config as Json | undefined;
        if (!oc || oc.effort === undefined) continue;
        if (wantEffort) oc.effort = wantEffort;
        else {
          delete oc.effort;
          if (Object.keys(oc).length === 0) delete m.output_config;
        }
      }
      // An effort-only system message with nothing left to say is invalid; drop it.
      body.messages = messages.filter((m) => !(m?.role === "system" && Array.isArray(m.content) && m.content.length === 0 && !m.output_config));
    } else {
      // Fold operator text into the top-level system prompt and drop the messages the model would reject.
      const extra = messages.filter((m) => m?.role === "system").map((m) => anthropicText(m.content)).filter(Boolean);
      if (extra.length) {
        const system = Array.isArray(body.system) ? (body.system as Json[]) : typeof body.system === "string" ? [{ type: "text", text: body.system }] : [];
        body.system = [...system, ...extra.map((text) => ({ type: "text", text }))];
      }
      body.messages = messages.filter((m) => m?.role !== "system");
    }
  }
  if (!supportsEffort) {
    delete body.thinking;
    const cm = body.context_management as Json | undefined;
    if (cm && Array.isArray(cm.edits)) {
      const edits = (cm.edits as Json[]).filter((e) => !String(e.type ?? "").startsWith("clear_thinking"));
      cm.edits = edits;
      if (edits.length === 0) delete body.context_management;
    }
  }
  const oc = (body.output_config as Json | undefined) ?? {};
  if (wantEffort) oc.effort = wantEffort;
  else delete oc.effort;
  if (Object.keys(oc).length) body.output_config = oc;
  else delete body.output_config;
  return body;
}

/* ------------------------------ OpenAI Responses (Codex) ------------------------------ */

function responsesText(content: unknown): string {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content
    .map((p) => (p && typeof p === "object" && ["text", "input_text", "output_text"].includes(String((p as Json).type)) ? String((p as Json).text ?? "") : ""))
    .filter(Boolean)
    .join("\n");
}

/** User text that starts a new Codex turn, or null for tool-output continuations. */
export function codexNewTurn(body: Json): string | null {
  const input = body.input;
  if (!Array.isArray(input)) return null;
  for (const item of [...(input as Json[])].reverse()) {
    if (item?.type === "function_call_output" || item?.type === "custom_tool_call_output") return null;
    if (item?.role !== "user") continue;
    const text = cleanPrompt(responsesText(item.content));
    if (text) return text;
  }
  return null;
}

export function codexFeatures(body: Json): Features {
  const input = (Array.isArray(body.input) ? body.input : []) as Json[];
  const turns = input
    .filter((i) => typeof i.role === "string")
    .map((i) => ({ role: String(i.role), text: cleanPrompt(responsesText(i.content)) }))
    .filter((t) => t.text);
  const lastUser = [...turns].reverse().find((t) => t.role === "user")?.text ?? "";
  const tools = (Array.isArray(body.tools) ? body.tools : []) as Json[];
  const hasImages = input.some((i) => Array.isArray(i.content) && (i.content as Json[]).some((p) => p?.type === "input_image"));
  return {
    inputTokens: Math.ceil(JSON.stringify(body).length / 4),
    turns: turns.length,
    hasImages,
    hasTools: tools.length > 0,
    toolNames: tools.map((t) => String(t.name ?? t.type ?? "")).filter(Boolean).slice(0, 40),
    requestedMaxTokens: typeof body.max_output_tokens === "number" ? body.max_output_tokens : undefined,
    systemExcerpt: truncate(typeof body.instructions === "string" ? body.instructions : "", 1500),
    recent: turns.slice(-6).map((t) => ({ role: t.role, text: truncate(t.text, 1500) })),
    lastUser: truncate(lastUser, 8000),
  };
}

/** Codex pins a conversation with `prompt_cache_key`; older builds only via turn metadata or content. */
export function codexConversationKey(body: Json): string {
  if (typeof body.prompt_cache_key === "string" && body.prompt_cache_key) return conversationKey(body.prompt_cache_key);
  const meta = body.client_metadata as Json | undefined;
  const turnMeta = meta?.["x-codex-turn-metadata"];
  if (typeof turnMeta === "string" && turnMeta) return conversationKey(turnMeta);
  const firstUser = (Array.isArray(body.input) ? (body.input as Json[]) : []).find((i) => i.role === "user");
  return conversationKey(
    typeof body.instructions === "string" ? body.instructions.slice(0, 2000) : undefined,
    firstUser ? responsesText(firstUser.content).slice(0, 2000) : undefined,
  );
}

/**
 * Point a Codex Responses request at `spec` and set the reasoning effort it supports.
 * OpenAI rejects `minimal` when built-in tools such as web_search are attached, and Codex always
 * attaches them, so the floor for Codex is `low`.
 */
export function applyCodexModel(body: Json, spec: ModelSpec, effort: Effort | undefined): Json {
  body.model = spec.model;
  let e = effort;
  if ((e === "minimal" || e === "none") && spec.effort.includes("low")) e = "low";
  const reasoning = (body.reasoning as Json | undefined) ?? {};
  if (e && spec.effort.includes(e)) {
    reasoning.effort = e;
    body.reasoning = reasoning;
  } else if (spec.effort.length === 0) {
    delete body.reasoning;
  }
  return body;
}
