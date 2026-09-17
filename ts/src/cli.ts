#!/usr/bin/env node
import { loadEnv } from "./env.js";
import { Frugal } from "./router.js";
import { startServer } from "./server.js";
import { summarize } from "./types.js";

loadEnv();

const [cmd = "help", ...rest] = process.argv.slice(2);

async function main() {
  switch (cmd) {
    case "serve": {
      const port = flag(rest, "--port");
      startServer(new Frugal(), port ? Number(port) : undefined);
      return;
    }
    case "route": {
      const text = rest.filter((a) => !a.startsWith("--")).join(" ");
      if (!text) return usage("route needs a prompt");
      let f = new Frugal({ ledgerPath: null });
      if (f.available().length === 0) {
        console.error("(no provider keys configured; dry run against the full catalog)");
        f = new Frugal({ ledgerPath: null, assumeAllProviders: true });
      }
      const d = await f.route({ model: "auto", messages: [{ role: "user", content: text }] });
      const s = summarize(d);
      if (rest.includes("--json")) return console.log(JSON.stringify({ ...s, candidates: d.candidates }, null, 2));
      console.log(`model    : ${s.model}${s.effort ? `  (effort: ${s.effort})` : ""}`);
      console.log(`task     : ${s.task}  tier ${s.tier}  difficulty ${s.difficulty}/4  reasoning ${s.reasoning}/3  stakes ${s.stakes}`);
      console.log(`source   : ${s.source}${d.judgment.error ? `  (${d.judgment.error})` : ""}  in ${d.judgment.latencyMs} ms`);
      console.log(`est cost : $${s.estimated_cost_usd}  vs baseline ${s.baseline_model} $${s.baseline_cost_usd}  (saves ~$${s.estimated_savings_usd})`);
      console.log("why      :");
      for (const r of d.rationale) console.log(`  - ${r}`);
      if (d.candidates.length) {
        console.log("ranked   :");
        for (const c of d.candidates) console.log(`  ${c.id.padEnd(34)} tier ${c.effectiveTier}  ~$${c.estimatedCost.toFixed(6)}`);
      }
      return;
    }
    case "models": {
      const f = new Frugal({ ledgerPath: null });
      const avail = new Set(f.available().map((m) => m.id));
      for (const m of f.catalog) {
        console.log(
          `${avail.has(m.id) ? "✔" : "·"} ${m.id.padEnd(34)} tier ${m.tier}  $${m.price.input}/$${m.price.output} per M  ${m.effort.length ? `effort ${m.effort.join(",")}` : "no effort"}`,
        );
      }
      console.log("\n✔ = provider key present");
      return;
    }
    case "stats": {
      const f = new Frugal();
      console.log(JSON.stringify(f.ledger.stats(), null, 2));
      return;
    }
    default:
      return usage();
  }
}

function flag(args: string[], name: string): string | undefined {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
}

function usage(msg?: string) {
  if (msg) console.error(`error: ${msg}\n`);
  console.log(`frugal — Jev decides which model and how much effort each LLM request deserves

  frugal serve [--port 8787]     start the OpenAI-compatible proxy
  frugal route "<prompt>" [--json]  dry run: show the decision for a prompt
  frugal models                  list the catalog and which providers have keys
  frugal stats                   cost ledger totals

Keys are read from .env (TYPESAFE_API_KEY, ANTHROPIC_API_KEY, OPENAI_API_KEY, OPENROUTER_API_KEY).`);
  if (msg) process.exit(2);
}

main().catch((err) => {
  console.error(err instanceof Error ? err.message : err);
  process.exit(1);
});
