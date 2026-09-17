import type { ChatRequest, ChatResponse, ChatUsage, DecisionSummary, Effort, ModelSpec, ToolCall } from "../types.js";

/** One OpenAI-style streaming chunk. */
export interface ChatChunk {
  id: string;
  object: "chat.completion.chunk";
  created: number;
  model: string;
  choices: Array<{
    index: number;
    delta: {
      role?: "assistant";
      content?: string | null;
      tool_calls?: Array<{ index: number; id?: string; type?: "function"; function?: Partial<ToolCall["function"]> }>;
    };
    finish_reason: "stop" | "length" | "tool_calls" | "content_filter" | null;
  }>;
  usage?: ChatUsage | null;
  dispatcher?: DecisionSummary;
}

export interface Provider {
  readonly name: string;
  complete(req: ChatRequest, spec: ModelSpec, effort?: Effort): Promise<ChatResponse>;
  stream(req: ChatRequest, spec: ModelSpec, effort?: Effort): AsyncGenerator<ChatChunk, void, undefined>;
}

export class UpstreamError extends Error {
  constructor(
    readonly provider: string,
    readonly status: number,
    message: string,
  ) {
    super(`${provider} upstream ${status}: ${message}`);
    this.name = "UpstreamError";
  }
}

/** Keys the proxy owns and must not forward. */
export const DISPATCHER_ONLY_FIELDS = ["dispatcher", "dispatcher_options"];
