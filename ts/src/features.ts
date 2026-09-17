import type { ChatMessage, ChatRequest, ContentPart, Features } from "./types.js";

const RECENT = 6;
const RECENT_CHARS = 1500;
const LAST_USER_CHARS = 8000;
const SYSTEM_CHARS = 1500;

/** Code-side facts about the request. No model involved. */
export function extractFeatures(req: ChatRequest): Features {
  let chars = 0;
  let hasImages = false;
  const systemParts: string[] = [];
  const turns: Array<{ role: string; text: string }> = [];

  for (const m of req.messages ?? []) {
    const text = messageText(m);
    chars += text.length + 16;
    if (Array.isArray(m.content) && m.content.some(isImage)) hasImages = true;
    if (m.tool_calls) chars += JSON.stringify(m.tool_calls).length;
    if (m.role === "system" || m.role === "developer") systemParts.push(text);
    else turns.push({ role: m.role, text });
  }
  if (req.tools?.length) chars += JSON.stringify(req.tools).length;

  const lastUser = [...turns].reverse().find((t) => t.role === "user")?.text ?? "";
  return {
    inputTokens: Math.ceil(chars / 4),
    turns: turns.length,
    hasImages,
    hasTools: (req.tools?.length ?? 0) > 0,
    toolNames: (req.tools ?? []).map((t) => t.function?.name).filter(Boolean).slice(0, 40),
    requestedMaxTokens: req.max_completion_tokens ?? req.max_tokens,
    systemExcerpt: truncate(systemParts.join("\n"), SYSTEM_CHARS),
    recent: turns.slice(-RECENT).map((t) => ({ role: t.role, text: truncate(t.text, RECENT_CHARS) })),
    lastUser: truncate(lastUser, LAST_USER_CHARS),
  };
}

export function messageText(m: ChatMessage): string {
  if (typeof m.content === "string") return m.content;
  if (!Array.isArray(m.content)) return "";
  return m.content
    .map((p) => (p.type === "text" ? (p as { text: string }).text : isImage(p) ? "[image]" : ""))
    .join("\n");
}

function isImage(p: ContentPart): boolean {
  return p.type === "image_url" || p.type === "input_image" || p.type === "image";
}

export function truncate(s: string, max: number): string {
  if (s.length <= max) return s;
  const head = Math.floor(max * 0.7);
  const tail = max - head;
  return `${s.slice(0, head)}\n…[${s.length - max} chars omitted]…\n${s.slice(-tail)}`;
}
