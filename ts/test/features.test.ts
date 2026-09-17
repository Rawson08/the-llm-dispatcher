import { test } from "node:test";
import assert from "node:assert/strict";
import { extractFeatures } from "../src/features.js";

test("features: tokens, images, tools, last user message", () => {
  const f = extractFeatures({
    model: "auto",
    messages: [
      { role: "system", content: "You are terse." },
      { role: "user", content: "first" },
      { role: "assistant", content: "ok" },
      {
        role: "user",
        content: [
          { type: "text", text: "what is in this picture?" },
          { type: "image_url", image_url: { url: "data:image/png;base64,AAAA" } },
        ],
      },
    ],
    tools: [{ type: "function", function: { name: "search", parameters: { type: "object" } } }],
    max_tokens: 300,
  });
  assert.equal(f.hasImages, true);
  assert.equal(f.hasTools, true);
  assert.deepEqual(f.toolNames, ["search"]);
  assert.equal(f.turns, 3);
  assert.equal(f.lastUser, "what is in this picture?\n[image]");
  assert.equal(f.systemExcerpt, "You are terse.");
  assert.equal(f.requestedMaxTokens, 300);
  assert.ok(f.inputTokens > 20);
});

test("features: long messages are truncated head and tail", () => {
  const big = "x".repeat(20_000);
  const f = extractFeatures({ model: "auto", messages: [{ role: "user", content: big }] });
  assert.ok(f.lastUser.length < 8_200);
  assert.ok(f.lastUser.includes("chars omitted"));
});
