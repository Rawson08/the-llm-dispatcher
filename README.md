# frugal

**Jev decides which model, and how hard it should think, before every LLM call.**

frugal is a drop-in, OpenAI-compatible proxy. Point any client at it with `model: "auto"` and,
for each request, it asks [Jev](https://typesafe.ai) (TypeSafe's System One model) five typed
questions about the conversation, then picks the cheapest model in your catalog that can do the
job and the lowest reasoning effort that job deserves. A greeting goes to a $0.07/M model at
minimal effort. A production database incident goes to a frontier model at `xhigh`. You pay for
capability only when the request needs it, and a ledger shows what you saved.

Two implementations with identical behaviour, sharing one catalog and one `.env`:

| | TypeScript (`ts/`) | C# (`csharp/`) |
|---|---|---|
| Runtime | Node 20.12+ | .NET 10 |
| Anthropic | official `@anthropic-ai/sdk` | official `Anthropic` NuGet |
| Jev | official `@typesafe-ai/sdk` | HTTP API (no official C# SDK yet) |
| OpenAI / OpenRouter / any OpenAI-compatible | built-in `fetch` | `HttpClient` |
| Tests | `npm test` (node:test) | `dotnet test` (xunit) |

## Why Jev and not another LLM?

Routing with an LLM costs a slow, expensive call before the real call. Jev returns typed answers
with calibrated probabilities in a few hundred milliseconds for $0.042 per million input tokens,
with free output. A routing decision on a typical request costs well under a hundredth of a cent
and adds roughly 250 to 400 ms. The decision is data, not prose, so the policy lives in ordinary
code you can read, test, and tune.

## How a decision is made

1. **Code extracts facts**: token estimate, whether images or tools are present, requested
   `max_tokens`, the last user message, the system prompt, and recent turns.
2. **Jev answers five questions in one call** (see `ts/src/judge.ts` / `csharp/Frugal/Judge.cs`):
   - `task` (choice): coding, writing, conversation, analysis, math, extraction, summarization, creative, agentic, other
   - `difficulty` (score 0 to 4): trivial, routine, moderate, hard, frontier
   - `reasoning` (score 0 to 3): none, light, substantial, deep
   - `stakes` (yes/no probability): would a subtly wrong answer be costly?
   - `output_size` (score 0 to 2): short, medium, long
3. **Policy turns answers into a tier and an effort** (`policy.ts` / `Policy.cs`):
   - `need = 0.55·difficulty/4 + 0.30·reasoning/3 + 0.15·stakes` selects tier 1, 2 or 3
   - high stakes never routes to the economy tier
   - low confidence on difficulty bumps one tier up, because Jev said "I'm not sure"
   - effort follows the reasoning score, floored at `high` for hard tasks, `max` only for hard, deep, high-stakes work
   - if Jev is unreachable the fallback is deliberately conservative: frontier tier, high effort
4. **Catalog filter and ranking**: drop models that lack vision or tools the request needs or
   whose context or output caps are too small, keep those whose effective tier meets the need
   (a model's `strengths` lift it one tier for that task), and pick the cheapest by expected cost.
   Effort is snapped to the nearest level the chosen model supports.
5. **The provider call** carries the effort the right way: `output_config.effort` for Anthropic,
   `reasoning_effort` for OpenAI-compatible APIs. Anthropic requests are translated to and from
   the Messages API, including tools, tool results, images, and streaming.

Every response includes a `frugal` object and `x-frugal-*` headers with the decision, the
rationale, and estimated cost versus a baseline model. Nothing about your prompts is stored;
the ledger keeps tokens, models, and dollars only.

## Quick start

```bash
cp .env.example .env      # paste your keys; the file is git-ignored
```

You need `TYPESAFE_API_KEY` (create one at https://console.typesafe.ai) plus at least one of
`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `OPENROUTER_API_KEY`. Models whose provider has no key are
simply not candidates.

### TypeScript

```bash
cd ts
npm install
npm run route -- "Write a limerick about tabs versus spaces"   # dry run: decision only
npm run serve                                                   # proxy on http://localhost:8787
```

### C#

```bash
cd csharp
dotnet run --project Frugal -- route "Write a limerick about tabs versus spaces"
dotnet run --project Frugal -- serve
```

### Use it from any OpenAI client

```bash
curl http://localhost:8787/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{"model":"auto","messages":[{"role":"user","content":"Explain CAP theorem in two sentences."}]}'
```

```python
from openai import OpenAI
client = OpenAI(base_url="http://localhost:8787/v1", api_key="unused-unless-FRUGAL_API_KEY-is-set")
r = client.chat.completions.create(model="auto", messages=[{"role": "user", "content": "hi"}])
print(r.model, r.model_extra["frugal"])
```

Streaming works (`"stream": true`). Requests that name a concrete catalog model such as
`anthropic/claude-sonnet-5` are forwarded unchanged and marked `passthrough`, unless
`FRUGAL_ROUTE_ALL=1`.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| POST | `/v1/chat/completions` | OpenAI-compatible completion; `model: "auto"` routes |
| POST | `/v1/route` | Decision only, no upstream call. Cheap way to see what frugal would do |
| GET | `/v1/models` | `frugal/auto` plus every model with credentials |
| GET | `/stats` | Ledger totals: spend, baseline spend, Jev spend, savings, per model, per task |
| GET | `/health` | Liveness and whether Jev routing is enabled |

Per-request overrides go in the body under `frugal_options`:

```json
{ "model": "auto", "messages": [...], "frugal_options": { "min_tier": 2, "max_tier": 3, "baseline": "openai/gpt-6-astra" } }
```

## The catalog

`models.json` at the repo root is shared by both implementations. Each entry has a provider,
the provider's model string, prices per million tokens, context and output caps, whether it
takes images and tools, the effort levels it accepts, and a **tier**: your belief about its
general quality (1 economy, 2 standard, 3 frontier). `strengths` lifts a model one tier for the
listed tasks; `weaknesses` lowers it. Prices were checked on 2026-09-17 against Anthropic's price
list and OpenRouter's public models API. Edit the file freely; both implementations validate it on
load. Point `FRUGAL_CATALOG` at your own file to keep a private catalog.

Tiers are the part you should tune. They encode a quality opinion, not a measurement. Run your own
traffic through `/v1/route` for a day, look at the ledger, and move models between tiers until
the decisions match what your team would choose by hand.

## Configuration

All configuration is environment variables, loaded from `.env` in the working directory or any
parent up to three levels (so both `ts/` and `csharp/` find the repo-root file). See
`.env.example` for the full list. frugal reports whether a key is present and never its value.

## Library use

TypeScript:

```ts
import { Frugal } from "frugal-router";
const frugal = new Frugal();
const decision = await frugal.route({ model: "auto", messages });   // no upstream call
const { response } = await frugal.complete(request);                 // route + call + ledger
```

C#:

```csharp
var frugal = new FrugalRouter();
var decision = await frugal.RouteAsync(request);
var (_, response) = await frugal.CompleteAsync(request);
```

## Prior art

Jev launched days before this project and several routers appeared at once. If one of these fits
you better, use it:

- [prismhq/jev-router](https://github.com/prismhq/jev-router): Python, LiteLLM transport, model choice only
- [gargpratyush/jev-router](https://github.com/gargpratyush/jev-router): wraps the Claude Code and Codex CLIs with two-tier routing
- [mejiasd3v/pi-jev-router](https://github.com/mejiasd3v/pi-jev-router): model plus thinking level for the Pi coding agent via Vercel AI Gateway
- [BunsDev/typesafe-router](https://github.com/BunsDev/typesafe-router): TypeScript library that picks among abstract option ids
- [tylerjharden/ailerix](https://github.com/tylerjharden/ailerix): hosted OpenRouter-style product on an Artificial Analysis Pareto frontier

frugal differs in combining all of: a general OpenAI-compatible proxy, native Anthropic and
OpenAI-compatible transports with no gateway dependency, model **and** effort selection with the
effort actually applied per provider, a transparent code-owned policy with confidence gating, a
cost ledger, and two first-class language implementations.

## Caveats

- Jev's judgments are calibrated but not infallible. Keep `FRUGAL_BASELINE` honest and watch
  `/stats`. If quality drops for a task type, raise the tier thresholds or add `strengths`.
- Prompt caching is model-scoped. Routing a long conversation across several models forfeits cache
  reuse. For agentic sessions, prefer pinning a model per session (send it explicitly) and letting
  frugal choose only the effort, or route only the first turn.
- Anthropic's `xhigh` and `max` effort levels, forced tool choice, and sampling parameters are
  model-dependent; the catalog and the provider bridge encode what is known as of September 2026.
- The proxy holds your provider keys. Set `FRUGAL_API_KEY` before exposing it beyond localhost.

## License

MIT.
