import type { ChatRequest, ChatResponse, Effort, ModelSpec } from "../types.js";
import { type ChatChunk, DISPATCHER_ONLY_FIELDS, type Provider, UpstreamError } from "./types.js";

export interface OpenAICompatibleOptions {
  name: string;
  baseURL: string;
  apiKeyEnv: string;
  extraHeaders?: Record<string, string>;
  /** OpenAI's newer models reject `max_tokens`; rename it. */
  useMaxCompletionTokens?: boolean;
  timeoutMs?: number;
}

/**
 * Any provider that speaks the OpenAI chat-completions dialect:
 * OpenAI itself, OpenRouter, Groq, Together, vLLM, and so on.
 */
export class OpenAICompatibleProvider implements Provider {
  readonly name: string;
  constructor(private readonly o: OpenAICompatibleOptions) {
    this.name = o.name;
  }

  private headers(): Record<string, string> {
    const key = process.env[this.o.apiKeyEnv];
    if (!key) throw new UpstreamError(this.name, 0, `${this.o.apiKeyEnv} is not set`);
    return { "content-type": "application/json", authorization: `Bearer ${key}`, ...(this.o.extraHeaders ?? {}) };
  }

  private body(req: ChatRequest, spec: ModelSpec, effort: Effort | undefined, stream: boolean): Record<string, unknown> {
    const body: Record<string, unknown> = { ...req, model: spec.model, stream };
    for (const k of DISPATCHER_ONLY_FIELDS) delete body[k];
    if (effort && spec.effort.length) body.reasoning_effort = effort;
    else delete body.reasoning_effort;
    if (this.o.useMaxCompletionTokens && body.max_tokens !== undefined && body.max_completion_tokens === undefined) {
      body.max_completion_tokens = body.max_tokens;
      delete body.max_tokens;
    }
    if (stream) body.stream_options = { ...(req.stream_options ?? {}), include_usage: true };
    else delete body.stream_options;
    return body;
  }

  private async post(body: Record<string, unknown>): Promise<Response> {
    const res = await fetch(`${this.o.baseURL.replace(/\/$/, "")}/chat/completions`, {
      method: "POST",
      headers: this.headers(),
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(this.o.timeoutMs ?? 600_000),
    });
    if (!res.ok) {
      const text = (await res.text().catch(() => "")).slice(0, 2000);
      throw new UpstreamError(this.name, res.status, text || res.statusText);
    }
    return res;
  }

  async complete(req: ChatRequest, spec: ModelSpec, effort?: Effort): Promise<ChatResponse> {
    const res = await this.post(this.body(req, spec, effort, false));
    return (await res.json()) as ChatResponse;
  }

  async *stream(req: ChatRequest, spec: ModelSpec, effort?: Effort): AsyncGenerator<ChatChunk, void, undefined> {
    const res = await this.post(this.body(req, spec, effort, true));
    if (!res.body) throw new UpstreamError(this.name, res.status, "empty body");
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buf = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buf += decoder.decode(value, { stream: true });
      let nl: number;
      while ((nl = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, nl).trim();
        buf = buf.slice(nl + 1);
        if (!line.startsWith("data:")) continue;
        const data = line.slice(5).trim();
        if (data === "[DONE]") return;
        try {
          yield JSON.parse(data) as ChatChunk;
        } catch {
          /* ignore keep-alives and partial lines */
        }
      }
    }
  }
}

export function openaiProvider(): OpenAICompatibleProvider {
  return new OpenAICompatibleProvider({
    name: "openai",
    baseURL: process.env.OPENAI_BASE_URL ?? "https://api.openai.com/v1",
    apiKeyEnv: "OPENAI_API_KEY",
    useMaxCompletionTokens: true,
  });
}

export function openrouterProvider(): OpenAICompatibleProvider {
  return new OpenAICompatibleProvider({
    name: "openrouter",
    baseURL: "https://openrouter.ai/api/v1",
    apiKeyEnv: "OPENROUTER_API_KEY",
    extraHeaders: { "HTTP-Referer": "https://github.com/the-llm-dispatcher", "X-Title": "dispatcher" },
  });
}
