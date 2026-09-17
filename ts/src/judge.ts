import { TypeSafeClient, choice, noul, score } from "@typesafe-ai/sdk";
import type { Features, Judgment, Task } from "./types.js";
import { ENV, has } from "./env.js";

/**
 * Asks Jev five independent questions about the request in one call.
 * Jev sees the conversation text; frugal's code owns the policy.
 */
export class Judge {
  private client: TypeSafeClient | null;
  readonly timeoutMs: number;

  constructor(opts: { client?: TypeSafeClient | null; timeoutMs?: number } = {}) {
    this.timeoutMs = opts.timeoutMs ?? Number(process.env[ENV.jevTimeout] ?? 5000);
    if (opts.client !== undefined) this.client = opts.client;
    else this.client = has(ENV.typesafe) ? new TypeSafeClient({ timeout: this.timeoutMs }) : null;
  }

  get enabled(): boolean {
    return this.client !== null;
  }

  async judge(f: Features): Promise<Judgment> {
    const started = Date.now();
    if (!this.client) return fallback("no TYPESAFE_API_KEY configured", started);

    try {
      const res = await this.client.systemOne({ state: buildState(f), questions: QUESTIONS });
      const a = res.answers;
      return {
        task: {
          choice: a.task.choice as Task,
          confidence: a.task.confidence,
          probabilities: a.task.probabilities as Record<string, number>,
        },
        difficulty: { score: a.difficulty.score, confidence: a.difficulty.confidence },
        reasoning: { score: a.reasoning.score, confidence: a.reasoning.confidence },
        stakes: a.stakes.noul,
        outputSize: { score: a.output_size.score, confidence: a.output_size.confidence },
        source: "jev",
        jevModel: res.model,
        jevInputTokens: res.usage.input_tokens,
        latencyMs: Date.now() - started,
      };
    } catch (err) {
      return fallback(err instanceof Error ? err.message : String(err), started);
    }
  }
}

/** Conservative judgment used when Jev is unavailable: routes to the safe tier. */
export function fallback(error: string, started = Date.now()): Judgment {
  return {
    task: { choice: "other", confidence: 0, probabilities: {} },
    difficulty: { score: 3, confidence: 0 },
    reasoning: { score: 2, confidence: 0 },
    stakes: 0.5,
    outputSize: { score: 1, confidence: 0 },
    source: "fallback",
    jevInputTokens: 0,
    latencyMs: Date.now() - started,
    error,
  };
}

function buildState(f: Features) {
  return {
    conversation: {
      system_prompt: f.systemExcerpt || null,
      turns_so_far: f.turns,
      recent_messages: f.recent,
      latest_user_message: f.lastUser,
    },
    request: {
      tools_available_to_assistant: f.toolNames,
      includes_images: f.hasImages,
    },
  };
}

export const QUESTIONS = {
  task: choice(
    {
      question:
        "What kind of work is the AI assistant being asked to do in `conversation.latest_user_message`, read in the context of the rest of `conversation`?",
      note: "Judge the work the model must perform, not the topic being discussed.",
    },
    {
      coding: "Write, modify, review, debug or explain source code, configuration, or shell commands",
      writing: "Draft or edit prose for a purpose: emails, posts, documentation, reports, cover letters",
      conversation: "Casual chat, greetings, quick opinions, small talk, or simple one-line factual questions",
      analysis: "Analyze, compare, plan, or reason about a situation, decision, design, or argument",
      math: "Mathematics, quantitative calculation, formal logic, or algorithmic problem solving",
      extraction: "Classify, label, or pull specific fields or structured data out of provided text",
      summarization: "Summarize, condense, or translate provided text without adding new content",
      creative: "Fiction, poetry, jokes, brainstorming, names, or other imaginative content",
      agentic: "Carry out a multi-step task using the available tools, such as browsing, running commands, or editing files",
      other: "None of the above fits well",
    },
  ),
  difficulty: score(
    "How difficult is it for an AI language model to produce a fully correct, high-quality response to `conversation.latest_user_message` given `conversation`?",
    [
      "Trivial: a greeting, acknowledgement, one-line factual answer, or simple rewording; almost any model gets it right",
      "Routine: a common, well-specified task such as a short email, a small function, a plain explanation, or a basic summary",
      "Moderate: needs several steps or careful attention to detail, such as a multi-part document, a medium-sized code change, or a comparison with tradeoffs",
      "Hard: needs expert knowledge, subtle judgment, or long chains of dependent steps, such as debugging a tricky bug, designing a system, or a rigorous analysis",
      "Frontier: research-level or extremely intricate; even the strongest models frequently make mistakes",
    ],
  ),
  reasoning: score(
    "How much deliberate, step-by-step reasoning does a correct response require, as opposed to recall, rewording, or pattern completion?",
    [
      "None: recall, rewording, formatting, or casual conversation",
      "Light: a few obvious steps or simple lookups",
      "Substantial: careful multi-step reasoning where an early mistake propagates, such as non-trivial code logic or quantitative analysis",
      "Deep: extended deliberation, exploring alternatives, proofs, tricky debugging, or planning with many interacting constraints",
    ],
  ),
  stakes: noul(
    "Would a subtly wrong or low-quality response cause meaningful harm or cost? Consider medical, legal, financial, security, or production-code contexts, and cases where the user clearly signals the answer matters a great deal.",
  ),
  output_size: score("How long does a good response to `conversation.latest_user_message` need to be?", [
    "Short: a sentence to a paragraph",
    "Medium: several paragraphs, about a page, or a function-sized block of code",
    "Long: a multi-page document, a large code file, or many files",
  ]),
};
