# the-llm-dispatcher

Jev-powered LLM router. Two shapes share one decision engine: an OpenAI-compatible API proxy
that routes across API-key providers (Anthropic, OpenAI, OpenRouter, any OpenAI-compatible or
local endpoint declared in `models.json`), and CLI wrappers (`llm-dispatcher claude`,
`llm-dispatcher codex`) that launch the official CLIs with their own subscription logins and
choose the model and effort per turn through a loopback proxy.

Two implementations with identical behaviour: `ts/` (Node, TypeScript) and `csharp/` (.NET 10).
They share `models.json` and `.env` at the root.

## Working on this project

- Use the **typesafe-ai** skill (plugin `typesafe@typesafe-ai`) for anything touching the Jev
  questions, state shape, or confidence handling. Live docs: https://docs.typesafe.ai/llms.txt
- Use the **claude-api** skill for anything touching the Anthropic provider bridge or the
  Claude Code wrapper's request rewriting.
- Keep the two implementations in lock-step. A change to the questions (`ts/src/judge.ts`,
  `csharp/Dispatcher/Judge.cs`), the policy (`ts/src/policy.ts`, `csharp/Dispatcher/Policy.cs`),
  or the wrapper rewriting (`ts/src/wrappers/turns.ts`, `csharp/Dispatcher/Wrappers/Turns.cs`)
  must land in both, with the matching test in `ts/test/` and `csharp/Dispatcher.Tests/`.
- Policy is pure code with no I/O. Put judgment in Jev questions, rules in policy, facts in the
  catalog. Do not encode model names in Jev questions.
- Never log, print, or read API key values or CLI login tokens. `.env` is git-ignored; only
  report presence. The wrappers forward CLI headers without inspecting them.
- The wrappers must only ever route to models the wrapped CLI's own service offers (Claude models
  for Claude Code, OpenAI models for Codex). Keep the README "Restrictions" section accurate.
- Prompt text must never reach the ledger or error messages.

## Commands

TypeScript (`cd ts`): `npm test`, `npm run typecheck`, `npm run route -- "<prompt>"`, `npm run serve`,
`npx tsx src/cli.ts claude`, `npx tsx src/cli.ts codex`, `npm run build`

C# (`cd csharp`): `dotnet test`, `dotnet run --project Dispatcher -- route "<prompt>"`,
`dotnet run --project Dispatcher -- serve`, `dotnet run --project Dispatcher -- claude`,
`dotnet run --project Dispatcher -- codex`

Set `DISPATCHER_DEBUG=1` to see each proxy decision on stderr. Live checks used so far:
`claude -p "What is 2+2? ..."` and `codex exec --skip-git-repo-check "..."` through each wrapper.
