import Anthropic from "@anthropic-ai/sdk";
import type { ChatMessage, ChatRequest, ChatResponse, ContentPart, Effort, ModelSpec, ToolCall } from "../types.js";
import { type ChatChunk, type Provider, UpstreamError } from "./types.js";

const DEFAULT_MAX_TOKENS = 16_000;
const DEFAULT_MAX_TOKENS_STREAM = 32_000;

/** Bridges the OpenAI chat dialect onto the Anthropic Messages API. */
export class AnthropicProvider implements Provider {
  readonly name = "anthropic";
  private client: Anthropic | null = null;

  private sdk(): Anthropic {
    if (!process.env.ANTHROPIC_API_KEY) throw new UpstreamError(this.name, 0, "ANTHROPIC_API_KEY is not set");
    return (this.client ??= new Anthropic());
  }

  async complete(req: ChatRequest, spec: ModelSpec, effort?: Effort): Promise<ChatResponse> {
    const params = toAnthropicParams(req, spec, effort, false);
    try {
      const msg = await this.sdk().messages.create(params);
      return fromAnthropicMessage(msg, spec);
    } catch (err) {
      throw wrap(err);
    }
  }

  async *stream(req: ChatRequest, spec: ModelSpec, effort?: Effort): AsyncGenerator<ChatChunk, void, undefined> {
    const params = toAnthropicParams(req, spec, effort, true);
    const created = Math.floor(Date.now() / 1000);
    let id = `chatcmpl-${Date.now()}`;
    let inputTokens = 0;
    let outputTokens = 0;
    let finish: ChatChunk["choices"][0]["finish_reason"] = null;
    // Anthropic block index -> OpenAI tool_call index
    const toolIndex = new Map<number, number>();
    let nextTool = 0;

    const chunk = (delta: ChatChunk["choices"][0]["delta"], finish_reason: ChatChunk["choices"][0]["finish_reason"] = null): ChatChunk => ({
      id,
      object: "chat.completion.chunk",
      created,
      model: spec.model,
      choices: [{ index: 0, delta, finish_reason }],
    });

    let stream: ReturnType<Anthropic["messages"]["stream"]>;
    try {
      stream = this.sdk().messages.stream(params);
    } catch (err) {
      throw wrap(err);
    }

    try {
      yield chunk({ role: "assistant", content: "" });
      for await (const ev of stream) {
        switch (ev.type) {
          case "message_start":
            id = ev.message.id;
            inputTokens = ev.message.usage.input_tokens;
            break;
          case "content_block_start":
            if (ev.content_block.type === "tool_use") {
              const idx = nextTool++;
              toolIndex.set(ev.index, idx);
              yield chunk({
                tool_calls: [{ index: idx, id: ev.content_block.id, type: "function", function: { name: ev.content_block.name, arguments: "" } }],
              });
            }
            break;
          case "content_block_delta":
            if (ev.delta.type === "text_delta") yield chunk({ content: ev.delta.text });
            else if (ev.delta.type === "input_json_delta") {
              const idx = toolIndex.get(ev.index);
              if (idx !== undefined) yield chunk({ tool_calls: [{ index: idx, function: { arguments: ev.delta.partial_json } }] });
            }
            break;
          case "message_delta":
            finish = mapStop(ev.delta.stop_reason);
            outputTokens = ev.usage.output_tokens;
            break;
          default:
            break;
        }
      }
    } catch (err) {
      throw wrap(err);
    }
    const last = chunk({}, finish ?? "stop");
    last.usage = { prompt_tokens: inputTokens, completion_tokens: outputTokens, total_tokens: inputTokens + outputTokens };
    yield last;
  }
}

function wrap(err: unknown): Error {
  if (err instanceof Anthropic.APIError) return new UpstreamError("anthropic", err.status ?? 0, err.message);
  return err instanceof Error ? err : new Error(String(err));
}

/* ---------------- request conversion ---------------- */

export function toAnthropicParams(
  req: ChatRequest,
  spec: ModelSpec,
  effort: Effort | undefined,
  stream: boolean,
): Anthropic.MessageCreateParamsNonStreaming {
  const system: string[] = [];
  const messages: Anthropic.MessageParam[] = [];

  for (const m of req.messages) {
    if (m.role === "system" || m.role === "developer") {
      system.push(textOf(m.content));
      continue;
    }
    if (m.role === "tool") {
      const block: Anthropic.ToolResultBlockParam = {
        type: "tool_result",
        tool_use_id: m.tool_call_id ?? "",
        content: textOf(m.content),
      };
      const prev = messages[messages.length - 1];
      if (prev && prev.role === "user" && Array.isArray(prev.content)) prev.content.push(block);
      else messages.push({ role: "user", content: [block] });
      continue;
    }
    if (m.role === "assistant") {
      const content: Anthropic.ContentBlockParam[] = [];
      const text = textOf(m.content);
      if (text) content.push({ type: "text", text });
      for (const tc of m.tool_calls ?? []) content.push(toolUse(tc));
      if (content.length) messages.push({ role: "assistant", content });
      continue;
    }
    messages.push({ role: "user", content: userContent(m.content) });
  }

  // Anthropic requires the first message to be from the user.
  if (messages.length === 0 || messages[0].role !== "user") messages.unshift({ role: "user", content: "(continue)" });

  const requested = req.max_completion_tokens ?? req.max_tokens;
  const maxTokens = Math.min(spec.maxOutput, requested ?? (stream ? DEFAULT_MAX_TOKENS_STREAM : DEFAULT_MAX_TOKENS));

  const params: Anthropic.MessageCreateParamsNonStreaming & Record<string, unknown> = {
    model: spec.model,
    max_tokens: maxTokens,
    messages,
  };
  if (system.length) params.system = system.join("\n\n");
  if (req.tools?.length) {
    params.tools = req.tools.map<Anthropic.Tool>((t) => ({
      name: t.function.name,
      description: t.function.description,
      input_schema: (t.function.parameters ?? { type: "object", properties: {} }) as Anthropic.Tool.InputSchema,
    }));
    const tc = toolChoice(req.tool_choice, spec);
    if (tc) params.tool_choice = tc;
  }
  if (req.stop) params.stop_sequences = Array.isArray(req.stop) ? req.stop : [req.stop];
  // Sampling parameters are rejected by Claude 4.7+ / 5.x; only older models accept them.
  if (/haiku-4-5|-4-6/.test(spec.model)) {
    if (req.temperature !== undefined) params.temperature = req.temperature;
    else if (req.top_p !== undefined) params.top_p = req.top_p;
  }
  // Anthropic accepts low..max; the catalog decides which of those a model supports.
  if (effort && spec.effort.includes(effort) && effort !== "none" && effort !== "minimal") {
    params.output_config = { effort };
  }
  return params;
}

function toolChoice(tc: ChatRequest["tool_choice"], spec: ModelSpec): Anthropic.ToolChoice | undefined {
  if (!tc || tc === "auto") return undefined;
  if (tc === "none") return { type: "none" };
  // Forced tool use is rejected by Claude Fable / Mythos; fall back to auto there.
  if (/fable|mythos/.test(spec.model)) return { type: "auto" };
  if (tc === "required") return { type: "any" };
  return { type: "tool", name: tc.function.name };
}

function toolUse(tc: ToolCall): Anthropic.ToolUseBlockParam {
  let input: unknown = {};
  try {
    input = tc.function.arguments ? JSON.parse(tc.function.arguments) : {};
  } catch {
    input = { _raw: tc.function.arguments };
  }
  return { type: "tool_use", id: tc.id, name: tc.function.name, input };
}

function textOf(c: ChatMessage["content"]): string {
  if (typeof c === "string") return c;
  if (!Array.isArray(c)) return "";
  return c.map((p) => (p.type === "text" ? (p as { text: string }).text : "")).join("");
}

function userContent(c: ChatMessage["content"]): string | Anthropic.ContentBlockParam[] {
  if (typeof c === "string") return c;
  if (!Array.isArray(c)) return "";
  const blocks: Anthropic.ContentBlockParam[] = [];
  for (const p of c as ContentPart[]) {
    if (p.type === "text") blocks.push({ type: "text", text: (p as { text: string }).text });
    else if (p.type === "image_url") {
      const url = (p as { image_url: { url: string } }).image_url.url;
      blocks.push({ type: "image", source: imageSource(url) });
    }
  }
  return blocks.length ? blocks : "";
}

function imageSource(url: string): Anthropic.ImageBlockParam["source"] {
  const m = /^data:(image\/(?:png|jpeg|gif|webp));base64,(.+)$/i.exec(url);
  if (m) return { type: "base64", media_type: m[1].toLowerCase() as Anthropic.Base64ImageSource["media_type"], data: m[2] };
  return { type: "url", url };
}

/* ---------------- response conversion ---------------- */

export function fromAnthropicMessage(msg: Anthropic.Message, spec: ModelSpec): ChatResponse {
  let text = "";
  const toolCalls: ToolCall[] = [];
  for (const block of msg.content) {
    if (block.type === "text") text += block.text;
    else if (block.type === "tool_use")
      toolCalls.push({ id: block.id, type: "function", function: { name: block.name, arguments: JSON.stringify(block.input ?? {}) } });
  }
  return {
    id: msg.id,
    object: "chat.completion",
    created: Math.floor(Date.now() / 1000),
    model: spec.model,
    choices: [
      {
        index: 0,
        message: { role: "assistant", content: text || (toolCalls.length ? null : ""), ...(toolCalls.length ? { tool_calls: toolCalls } : {}) },
        finish_reason: mapStop(msg.stop_reason),
      },
    ],
    usage: {
      prompt_tokens: msg.usage.input_tokens,
      completion_tokens: msg.usage.output_tokens,
      total_tokens: msg.usage.input_tokens + msg.usage.output_tokens,
    },
  };
}

function mapStop(reason: string | null | undefined): ChatResponse["choices"][0]["finish_reason"] {
  switch (reason) {
    case "end_turn":
    case "stop_sequence":
    case "pause_turn":
      return "stop";
    case "max_tokens":
      return "length";
    case "tool_use":
      return "tool_calls";
    case "refusal":
      return "content_filter";
    default:
      return null;
  }
}
