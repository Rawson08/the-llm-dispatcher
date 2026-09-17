import { test } from "node:test";
import assert from "node:assert/strict";
import { decide, desiredEffort, snapEffort } from "../src/policy.js";
import { fallback } from "../src/judge.js";
import type { Features, Judgment, ModelSpec } from "../src/types.js";

const catalog: ModelSpec[] = [
  { id: "a/frontier", provider: "anthropic", model: "frontier", name: "Frontier", tier: 3, price: { input: 10, output: 50 }, context: 1_000_000, maxOutput: 128_000, vision: true, tools: true, effort: ["low", "medium", "high", "xhigh", "max"] },
  { id: "a/standard", provider: "anthropic", model: "standard", name: "Standard", tier: 2, price: { input: 2, output: 10 }, context: 1_000_000, maxOutput: 128_000, vision: true, tools: true, effort: ["low", "medium", "high", "xhigh", "max"], strengths: ["coding"] },
  { id: "o/economy", provider: "openai", model: "economy", name: "Economy", tier: 1, price: { input: 0.2, output: 1.2 }, context: 400_000, maxOutput: 128_000, vision: true, tools: true, effort: ["minimal", "low", "medium", "high"] },
  { id: "r/blind-cheap", provider: "openrouter", model: "blind", name: "Blind cheap", tier: 1, price: { input: 0.07, output: 0.14 }, context: 1_000_000, maxOutput: 100_000, vision: false, tools: true, effort: [] },
];

const features = (over: Partial<Features> = {}): Features => ({
  inputTokens: 500, turns: 1, hasImages: false, hasTools: false, toolNames: [], systemExcerpt: "", recent: [], lastUser: "hi", ...over,
});

const judgment = (over: Partial<Judgment> = {}): Judgment => ({
  task: { choice: "conversation", confidence: 0.9, probabilities: {} },
  difficulty: { score: 0.3, confidence: 0.9 },
  reasoning: { score: 0.2, confidence: 0.9 },
  stakes: 0.05,
  outputSize: { score: 0.2, confidence: 0.9 },
  source: "jev", jevInputTokens: 300, latencyMs: 40,
  ...over,
});

test("trivial chat goes to the cheapest economy model with minimal effort", () => {
  const d = decide(features(), judgment(), catalog);
  assert.equal(d.tier, 1);
  assert.equal(d.model.id, "r/blind-cheap");
  assert.equal(d.effort, undefined); // model has no effort control
  assert.ok(d.estimate.savings > 0);
  assert.equal(d.estimate.baselineModel, "a/frontier");
});

test("images exclude models without vision", () => {
  const d = decide(features({ hasImages: true }), judgment(), catalog);
  assert.equal(d.model.id, "o/economy");
  assert.equal(d.effort, "minimal");
});

test("hard, reasoning-heavy work lands on the frontier tier at high effort", () => {
  const d = decide(
    features(),
    judgment({ task: { choice: "analysis", confidence: 0.8, probabilities: {} }, difficulty: { score: 3.6, confidence: 0.8 }, reasoning: { score: 2.7, confidence: 0.8 }, stakes: 0.6 }),
    catalog,
  );
  assert.equal(d.tier, 3);
  assert.equal(d.model.id, "a/frontier");
  assert.equal(d.effort, "xhigh");
});

test("a coding strength lifts the standard model into the frontier pool and it wins on price", () => {
  const d = decide(
    features(),
    judgment({ task: { choice: "coding", confidence: 0.9, probabilities: {} }, difficulty: { score: 3.5, confidence: 0.9 }, reasoning: { score: 2.4, confidence: 0.9 }, stakes: 0.5 }),
    catalog,
  );
  assert.equal(d.tier, 3);
  assert.equal(d.model.id, "a/standard");
  assert.equal(d.effort, "high");
});

test("high stakes never routes to the economy tier", () => {
  const d = decide(features(), judgment({ stakes: 0.85 }), catalog);
  assert.ok(d.tier >= 2);
  assert.notEqual(d.model.tier, 1);
});

test("low difficulty confidence bumps one tier for safety", () => {
  const d = decide(features(), judgment({ difficulty: { score: 0.5, confidence: 0.2 } }), catalog);
  assert.equal(d.tier, 2);
});

test("jev fallback is conservative: frontier tier, high effort", () => {
  const d = decide(features(), fallback("boom"), catalog);
  assert.equal(d.tier, 3);
  assert.equal(d.effort, "high");
  assert.ok(d.rationale.some((r) => r.includes("jev unavailable")));
});

test("explicit baseline changes the savings math", () => {
  const d = decide(features(), judgment(), catalog, { baselineId: "a/standard" });
  assert.equal(d.estimate.baselineModel, "a/standard");
});

test("requested max_tokens above a model's cap excludes it", () => {
  const d = decide(features({ requestedMaxTokens: 120_000 }), judgment(), catalog);
  assert.notEqual(d.model.id, "r/blind-cheap");
});

test("effort snapping prefers the next level up", () => {
  assert.equal(snapEffort("minimal", ["low", "medium", "high"]), "low");
  assert.equal(snapEffort("max", ["minimal", "low", "medium", "high"]), "high");
  assert.equal(snapEffort("medium", []), undefined);
  assert.equal(desiredEffort(0, 0, 0), "minimal");
  assert.equal(desiredEffort(3.5, 1.0, 0), "high");
  assert.equal(desiredEffort(3.8, 2.9, 0.9), "max");
});
