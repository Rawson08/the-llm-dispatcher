import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  anthropicFeatures, anthropicNewTurn, applyClaudeModel, applyCodexModel, cleanPrompt,
  codexConversationKey, codexFeatures, codexNewTurn, isAutoModel,
} from "../src/wrappers/turns.js";
import { readSavedModel, restoreSavedModel } from "../src/wrappers/claude.js";
import type { ModelSpec } from "../src/types.js";

const haiku: ModelSpec = { id: "anthropic/claude-haiku-4-5", provider: "anthropic", model: "claude-haiku-4-5", name: "Haiku", tier: 1, price: { input: 1, output: 5 }, context: 200_000, maxOutput: 64_000, vision: true, tools: true, effort: [] };
const opus: ModelSpec = { ...haiku, id: "anthropic/claude-opus-5", model: "claude-opus-5", tier: 3, effort: ["low", "medium", "high", "xhigh", "max"] };

const claudeBody = (last: unknown, tools = true) => ({
  model: "dispatcher-auto",
  max_tokens: 32000,
  system: [{ type: "text", text: "x-anthropic-billing-header: claude-code/2.1" }, { type: "text", text: "You are Claude Code." }],
  tools: tools ? [{ name: "Bash" }, { name: "Read" }] : [],
  thinking: { type: "adaptive" },
  output_config: { effort: "max" },
  context_management: { edits: [{ type: "clear_thinking_20251015" }, { type: "clear_tool_uses_20250919" }] },
  messages: [{ role: "user", content: "first" }, { role: "assistant", content: [{ type: "text", text: "ok" }] }, last],
});

test("claude: a fresh user turn is detected and system reminders are stripped", () => {
  const prompt = anthropicNewTurn(claudeBody({ role: "user", content: [{ type: "text", text: "<system-reminder>noise</system-reminder>fix the bug" }] }));
  assert.equal(prompt, "fix the bug");
});

test("claude: tool_result continuations and tool-less auxiliary calls are not turns", () => {
  assert.equal(anthropicNewTurn(claudeBody({ role: "user", content: [{ type: "tool_result", tool_use_id: "t1", content: "out" }] })), null);
  assert.equal(anthropicNewTurn(claudeBody({ role: "user", content: "summarize" }, false)), null);
  assert.equal(anthropicNewTurn(claudeBody({ role: "assistant", content: "hi" })), null);
});

test("claude: features skip the attribution block and count tools and turns", () => {
  const f = anthropicFeatures(claudeBody({ role: "user", content: "hello" }));
  assert.equal(f.systemExcerpt, "You are Claude Code.");
  assert.deepEqual(f.toolNames, ["Bash", "Read"]);
  assert.equal(f.turns, 3);
  assert.equal(f.lastUser, "hello");
  assert.equal(f.requestedMaxTokens, 32000);
});

test("claude: routing down to Haiku strips thinking, effort and thinking-clearing edits", () => {
  const body = applyClaudeModel(claudeBody({ role: "user", content: "hi" }) as Record<string, unknown>, haiku, "low");
  assert.equal(body.model, "claude-haiku-4-5");
  assert.equal(body.thinking, undefined);
  assert.equal(body.output_config, undefined);
  assert.deepEqual((body.context_management as { edits: unknown[] }).edits, [{ type: "clear_tool_uses_20250919" }]);
});

test("claude: routing to Opus applies the chosen effort and keeps thinking", () => {
  const body = applyClaudeModel(claudeBody({ role: "user", content: "hi" }) as Record<string, unknown>, opus, "medium");
  assert.equal(body.model, "claude-opus-5");
  assert.deepEqual(body.thinking, { type: "adaptive" });
  assert.deepEqual(body.output_config, { effort: "medium" });
});

test("claude: a trailing operator system message does not hide the user turn", () => {
  const body = claudeBody({ role: "user", content: [{ type: "text", text: "refactor auth" }] }) as Record<string, unknown>;
  (body.messages as unknown[]).push({ role: "system", content: [], output_config: { effort: "max" } });
  (body.messages as unknown[]).push({ role: "system", content: [{ type: "text", text: "Be terse." }] });
  assert.equal(anthropicNewTurn(body), "refactor auth");
  const f = anthropicFeatures(body);
  assert.equal(f.lastUser, "refactor auth");
});

test("claude: routing to Sonnet folds mid-conversation system messages into the system prompt", () => {
  const sonnet: ModelSpec = { ...opus, id: "anthropic/claude-sonnet-5", model: "claude-sonnet-5", tier: 2 };
  const body = claudeBody({ role: "user", content: "hi" }) as Record<string, unknown>;
  (body.messages as unknown[]).push({ role: "system", content: [], output_config: { effort: "max" } });
  (body.messages as unknown[]).push({ role: "system", content: [{ type: "text", text: "Be terse." }] });
  applyClaudeModel(body, sonnet, "low");
  const msgs = body.messages as Array<{ role: string }>;
  assert.equal(msgs.some((m) => m.role === "system"), false);
  const system = body.system as Array<{ text: string }>;
  assert.equal(system[system.length - 1].text, "Be terse.");
  assert.deepEqual(body.output_config, { effort: "low" });
});

test("claude: routing to Opus keeps operator messages and aligns per-message effort", () => {
  const body = claudeBody({ role: "user", content: "hi" }) as Record<string, unknown>;
  (body.messages as unknown[]).push({ role: "system", content: [], output_config: { effort: "max" } });
  (body.messages as unknown[]).push({ role: "system", content: [{ type: "text", text: "Be terse." }] });
  applyClaudeModel(body, opus, "medium");
  const msgs = body.messages as Array<{ role: string; output_config?: { effort: string } }>;
  const sys = msgs.filter((m) => m.role === "system");
  assert.equal(sys.length, 2);
  assert.equal(sys[0].output_config?.effort, "medium");
});

test("auto model detection tolerates Claude Code's [1m] suffix", () => {
  assert.equal(isAutoModel("dispatcher-auto", "dispatcher-auto"), true);
  assert.equal(isAutoModel("dispatcher-auto[1m]", "dispatcher-auto"), true);
  assert.equal(isAutoModel("claude-opus-5", "dispatcher-auto"), false);
});

const codexBody = (input: unknown[]) => ({
  model: "dispatcher-auto",
  instructions: "You are Codex.",
  reasoning: { effort: "xhigh", summary: "auto" },
  tools: [{ type: "function", name: "shell" }],
  prompt_cache_key: "conv-123",
  input,
});

test("codex: fresh turn vs tool-output continuation", () => {
  assert.equal(codexNewTurn(codexBody([{ role: "user", content: [{ type: "input_text", text: "<current_datetime>now</current_datetime>add tests" }] }])), "add tests");
  assert.equal(codexNewTurn(codexBody([{ role: "user", content: "add tests" }, { type: "function_call_output", call_id: "c", output: "ok" }])), null);
});

test("codex: features and conversation key", () => {
  const body = codexBody([{ role: "user", content: "add tests" }]);
  const f = codexFeatures(body);
  assert.equal(f.systemExcerpt, "You are Codex.");
  assert.deepEqual(f.toolNames, ["shell"]);
  assert.equal(f.lastUser, "add tests");
  assert.equal(codexConversationKey(body), codexConversationKey({ ...body, input: [] }), "prompt_cache_key pins the conversation");
});

test("codex: model and reasoning effort are rewritten", () => {
  const luna: ModelSpec = { ...opus, id: "openai/gpt-5.6-luna", provider: "openai", model: "gpt-5.6-luna", effort: ["minimal", "low", "medium", "high", "xhigh"] };
  const body = applyCodexModel(codexBody([{ role: "user", content: "hi" }]) as Record<string, unknown>, luna, "low");
  assert.equal(body.model, "gpt-5.6-luna");
  assert.deepEqual(body.reasoning, { effort: "low", summary: "auto" });
  const floored = applyCodexModel(codexBody([{ role: "user", content: "hi" }]) as Record<string, unknown>, luna, "minimal");
  assert.equal((floored.reasoning as { effort: string }).effort, "low", "minimal is rejected alongside Codex's built-in tools");
});

test("cleanPrompt removes both reminder styles", () => {
  assert.equal(cleanPrompt("<system_reminder>a</system_reminder> keep <system-reminder>b</system-reminder>"), "keep");
});

test("claude: the picker sentinel is never left as the saved default", () => {
  const dir = mkdtempSync(join(tmpdir(), "dispatcher-test-"));
  const file = join(dir, "settings.json");
  writeFileSync(file, JSON.stringify({ model: "claude-opus-5", other: 1 }));
  assert.equal(readSavedModel(file), "claude-opus-5");
  writeFileSync(file, JSON.stringify({ model: "dispatcher-auto", other: 1 }));
  assert.equal(readSavedModel(file), undefined, "a leftover sentinel is not a preference");
  assert.equal(restoreSavedModel("claude-opus-5", file), true);
  assert.deepEqual(JSON.parse(readFileSync(file, "utf8")), { model: "claude-opus-5", other: 1 });
  writeFileSync(file, JSON.stringify({ model: "dispatcher-auto" }));
  assert.equal(restoreSavedModel(undefined, file), true);
  assert.deepEqual(JSON.parse(readFileSync(file, "utf8")), {});
  writeFileSync(file, JSON.stringify({ model: "claude-sonnet-5" }));
  assert.equal(restoreSavedModel("claude-opus-5", file), false, "a real choice made during the session survives");
});
