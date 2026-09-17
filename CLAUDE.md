# the-llm-dispatcher

Jev-powered LLM router: an OpenAI-compatible proxy that picks the cheapest adequate model and the
lowest adequate reasoning effort for each request. Two implementations with identical behaviour:
`ts/` (Node, TypeScript) and `csharp/` (.NET 10). They share `models.json` and `.env` at the root.

## Working on this project

- Use the **typesafe-ai** skill (plugin `typesafe@typesafe-ai`) for anything touching the Jev
  questions, state shape, or confidence handling. Live docs: https://docs.typesafe.ai/llms.txt
- Use the **claude-api** skill for anything touching the Anthropic provider bridge.
- Keep the two implementations in lock-step. A change to the questions (`ts/src/judge.ts`,
  `csharp/Dispatcher/Judge.cs`) or the policy (`ts/src/policy.ts`, `csharp/Dispatcher/Policy.cs`) must
  land in both, with the matching test in `ts/test/` and `csharp/Dispatcher.Tests/`.
- Policy is pure code with no I/O. Put judgment in Jev questions, rules in policy, facts in the
  catalog. Do not encode model names in Jev questions.
- Never log, print, or read API key values. `.env` is git-ignored; only report presence.
- Prompt text must never reach the ledger or error messages.

## Commands

TypeScript (`cd ts`): `npm test`, `npm run typecheck`, `npm run route -- "<prompt>"`, `npm run serve`, `npm run build`

C# (`cd csharp`): `dotnet test`, `dotnet run --project Dispatcher -- route "<prompt>"`, `dotnet run --project Dispatcher -- serve`
