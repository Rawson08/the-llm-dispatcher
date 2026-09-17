import { forward, startLoopback } from "./proxy-util.js";
import { childEnv, resolveOnPath, runChild } from "./launch.js";
import { CODEX_STATUS_FILE, TurnRouter } from "./session.js";
import { CODEX_AUTO_MODEL, applyCodexModel, codexConversationKey, codexFeatures, codexNewTurn, isAutoModel } from "./turns.js";

const CHATGPT_UPSTREAM = "https://chatgpt.com/backend-api/codex";
const API_UPSTREAM = "https://api.openai.com/v1";
const PROVIDER_ID = "dispatcher";

/**
 * `llm-dispatcher codex [codex args...]`
 *
 * Launches the real Codex CLI with a temporary model provider that points at a loopback proxy
 * and reuses Codex's own OpenAI login (`requires_openai_auth`, a documented provider option).
 * The proxy rewrites `model` and `reasoning.effort` on fresh user turns that name the
 * Dispatcher model, forwards everything else unchanged, and streams responses back.
 */
export async function runCodex(args: string[]): Promise<number> {
  const cli = resolveOnPath("codex");
  if (!cli) {
    console.error("codex was not found on PATH. Install Codex: https://developers.openai.com/codex/cli");
    return 1;
  }
  const router = new TurnRouter({
    provider: "openai",
    allowList: process.env.DISPATCHER_CODEX_MODELS ?? "gpt-5.6-luna,gpt-5.6-terra,gpt-5.6-sol,gpt-6-astra",
    baselineModel: process.env.DISPATCHER_CODEX_BASELINE ?? "gpt-6-astra",
    statusFile: CODEX_STATUS_FILE,
  });

  const { port, close } = await startLoopback(async (req, res, raw) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    // Codex sends a ChatGPT account header when signed in with a subscription; API-key logins go to the platform API.
    const upstream = process.env.DISPATCHER_CODEX_UPSTREAM ?? (req.headers["chatgpt-account-id"] ? CHATGPT_UPSTREAM : API_UPSTREAM);
    const target = `${upstream}${url.pathname}${url.search}`;

    if (req.method === "GET" && url.pathname === "/models") return forwardModels(target, req, raw, res);
    if (!(req.method === "POST" && url.pathname === "/responses") || raw.length === 0) return forward(target, req, raw, res);

    let body: Record<string, unknown>;
    try {
      body = JSON.parse(raw.toString("utf8"));
    } catch {
      return forward(target, req, raw, res);
    }
    if (!isAutoModel(body.model, CODEX_AUTO_MODEL)) {
      debug(`/responses model=${String(body.model)} -> passthrough (concrete model)`);
      return forward(target, req, raw, res);
    }

    const key = codexConversationKey(body);
    const prompt = codexNewTurn(body);
    const decision = prompt ? await router.decide(key, codexFeatures(body)) : router.current(key);
    if (!decision) {
      const spec = router.safeDefault();
      applyCodexModel(body, spec, spec.effort.includes("high") ? "high" : undefined);
      debug(`/responses no decision yet -> safe default ${spec.model}`);
    } else {
      applyCodexModel(body, decision.model, decision.effort);
      debug(`/responses ${prompt ? "NEW TURN" : "continuation"} -> ${decision.model.model}${decision.effort ? ` @${decision.effort}` : ""} (${decision.judgment.source}, task ${decision.task}) via ${upstream}`);
    }
    return forward(target, req, Buffer.from(JSON.stringify(body)), res);
  });

  const userNamedModel = args.some((a) => a === "--model" || a === "-m" || a.startsWith("--model="));
  const codexArgs = [
    ...(userNamedModel ? [] : ["--model", CODEX_AUTO_MODEL]),
    "--config", `model_provider="${PROVIDER_ID}"`,
    "--config", `model_providers.${PROVIDER_ID}.name="Dispatcher"`,
    "--config", `model_providers.${PROVIDER_ID}.base_url="http://127.0.0.1:${port}"`,
    "--config", `model_providers.${PROVIDER_ID}.wire_api="responses"`,
    "--config", `model_providers.${PROVIDER_ID}.requires_openai_auth=true`,
    "--config", `model_providers.${PROVIDER_ID}.supports_websockets=false`,
    ...args,
  ];
  console.error(
    `[dispatcher] proxy on 127.0.0.1:${port}; jev ${router.judge.enabled ? "on" : "OFF (no TYPESAFE_API_KEY; safe default only)"}; models: ${router.models.map((m) => m.model).join(", ")}`,
  );
  try {
    return await runChild(cli, codexArgs, childEnv({}));
  } finally {
    close();
  }
}

function debug(msg: string): void {
  if (process.env.DISPATCHER_DEBUG) process.stderr.write(`[dispatcher] ${msg}\n`);
}

/**
 * Codex validates `--model` against the provider's catalog, so the Dispatcher row is added to the
 * list the upstream returns. Any other shape is forwarded untouched.
 */
async function forwardModels(target: string, req: import("node:http").IncomingMessage, raw: Buffer, res: import("node:http").ServerResponse) {
  const headers: Record<string, string> = {};
  for (const [k, v] of Object.entries(req.headers)) if (v && !["host", "content-length", "accept-encoding"].includes(k)) headers[k] = String(v);
  const upstream = await fetch(target, { headers: { ...headers, "accept-encoding": "identity" } });
  const text = await upstream.text();
  let out = text;
  try {
    const json = JSON.parse(text) as { models?: Array<Record<string, unknown>> };
    if (Array.isArray(json.models) && !json.models.some((m) => m.slug === CODEX_AUTO_MODEL)) {
      const template = json.models.find((m) => m.visibility === "list") ?? json.models[0];
      if (template) {
        json.models.unshift({
          ...template,
          slug: CODEX_AUTO_MODEL,
          display_name: "Dispatcher",
          description: "Jev picks the cheapest model and effort for each turn.",
          visibility: "list",
        });
        out = JSON.stringify(json);
      }
    }
  } catch {
    /* not the catalog shape; pass through */
  }
  res.writeHead(upstream.status, { "content-type": upstream.headers.get("content-type") ?? "application/json" });
  res.end(out);
  void raw;
}
