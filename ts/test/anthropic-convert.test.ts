import { test } from "node:test";
import assert from "node:assert/strict";
import { fromAnthropicMessage, toAnthropicParams } from "../src/providers/anthropic.js";
import type { ModelSpec } from "../src/types.js";

const spec: ModelSpec = {
  id: "anthropic/claude-sonnet-5", provider: "anthropic", model: "claude-sonnet-5", name: "Sonnet 5", tier: 2,
  price: { input: 2, output: 10 }, context: 1_000_000, maxOutput: 128_000, vision: true, tools: true,
  effort: ["low", "medium", "high", "xhigh", "max"],
};

test("openai chat -> anthropic params: system, tools, tool results, images, effort", () => {
  const p = toAnthropicParams(
    {
      model: "auto",
      messages: [
        { role: "system", content: "be brief" },
        { role: "user", content: [{ type: "text", text: "look" }, { type: "image_url", image_url: { url: "data:image/png;base64,QUJD" } }] },
        { role: "assistant", content: null, tool_calls: [{ id: "call_1", type: "function", function: { name: "lookup", arguments: '{"q":"x"}' } }] },
        { role: "tool", tool_call_id: "call_1", content: "42" },
      ],
      tools: [{ type: "function", function: { name: "lookup", description: "d", parameters: { type: "object", properties: {} } } }],
      tool_choice: "required",
      max_tokens: 500,
      temperature: 0.2,
    },
    spec,
    "medium",
    false,
  );
  assert.equal(p.system, "be brief");
  assert.equal(p.max_tokens, 500);
  assert.equal(p.messages.length, 3);
  assert.equal(p.messages[0].role, "user");
  const first = p.messages[0].content as Array<{ type: string }>;
  assert.deepEqual(first.map((b) => b.type), ["text", "image"]);
  assert.equal(p.messages[1].role, "assistant");
  const tu = (p.messages[1].content as Array<{ type: string; input?: unknown }>)[0];
  assert.equal(tu.type, "tool_use");
  assert.deepEqual(tu.input, { q: "x" });
  const tr = (p.messages[2].content as Array<{ type: string; tool_use_id?: string }>)[0];
  assert.equal(tr.type, "tool_result");
  assert.equal(tr.tool_use_id, "call_1");
  assert.deepEqual(p.tool_choice, { type: "any" });
  assert.equal(p.temperature, undefined, "sampling params are dropped for 5.x models");
  assert.deepEqual((p as unknown as Record<string, unknown>).output_config, { effort: "medium" });
});

test("forced tool choice is downgraded to auto on Fable", () => {
  const p = toAnthropicParams(
    { model: "auto", messages: [{ role: "user", content: "x" }], tools: [{ type: "function", function: { name: "t" } }], tool_choice: "required" },
    { ...spec, model: "claude-fable-5-1" },
    undefined,
    true,
  );
  assert.deepEqual(p.tool_choice, { type: "auto" });
  assert.equal(p.max_tokens, 32_000);
});

test("anthropic message -> openai response", () => {
  const r = fromAnthropicMessage(
    {
      id: "msg_1", type: "message", role: "assistant", model: "claude-sonnet-5",
      content: [
        { type: "text", text: "Sure." },
        { type: "tool_use", id: "toolu_1", name: "lookup", input: { q: "x" } },
      ],
      stop_reason: "tool_use", stop_sequence: null,
      usage: { input_tokens: 10, output_tokens: 5, cache_creation_input_tokens: null, cache_read_input_tokens: null, server_tool_use: null, service_tier: null },
    } as never,
    spec,
  );
  assert.equal(r.choices[0].finish_reason, "tool_calls");
  assert.equal(r.choices[0].message.content, "Sure.");
  assert.equal(r.choices[0].message.tool_calls?.[0].function.arguments, '{"q":"x"}');
  assert.equal(r.usage.total_tokens, 15);
});
