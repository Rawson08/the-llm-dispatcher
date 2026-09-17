import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { ENV, has } from "./env.js";
import { UpstreamError } from "./providers/types.js";
import { Frugal } from "./router.js";
import type { ChatRequest } from "./types.js";
import { summarize } from "./types.js";

const MAX_BODY = 32 * 1024 * 1024;

/**
 * OpenAI-compatible HTTP proxy.
 *   POST /v1/chat/completions   model:"auto" -> Jev routes; concrete model -> passthrough
 *   POST /v1/route              decision only, no upstream call
 *   GET  /v1/models             "frugal/auto" plus every model with credentials
 *   GET  /stats                 ledger aggregates
 *   GET  /health
 */
export function createFrugalServer(frugal: Frugal) {
  return createServer(async (req, res) => {
    cors(res);
    if (req.method === "OPTIONS") return end(res, 204);
    const url = new URL(req.url ?? "/", "http://localhost");
    try {
      if (!authorized(req)) return json(res, 401, { error: { message: "unauthorized", type: "auth" } });

      if (req.method === "GET" && url.pathname === "/health") return json(res, 200, { ok: true, jev: frugal.judge.enabled });
      if (req.method === "GET" && url.pathname === "/stats") return json(res, 200, frugal.ledger.stats());
      if (req.method === "GET" && (url.pathname === "/v1/models" || url.pathname === "/models")) {
        const now = Math.floor(Date.now() / 1000);
        const data = [
          { id: "frugal/auto", object: "model", created: now, owned_by: "frugal" },
          ...frugal.available().map((m) => ({ id: m.id, object: "model", created: now, owned_by: m.provider })),
        ];
        return json(res, 200, { object: "list", data });
      }
      if (req.method === "POST" && (url.pathname === "/v1/route" || url.pathname === "/route")) {
        const body = (await readJson(req)) as ChatRequest;
        if (!body.model) body.model = "auto";
        const d = await frugal.route(body);
        return json(res, 200, { ...summarize(d), candidates: d.candidates, estimate: d.estimate });
      }
      if (req.method === "POST" && (url.pathname === "/v1/chat/completions" || url.pathname === "/chat/completions")) {
        const body = (await readJson(req)) as ChatRequest;
        if (!Array.isArray(body.messages)) return json(res, 400, { error: { message: "messages[] required", type: "invalid_request_error" } });
        body.model ||= "auto";
        const decision = await frugal.route(body);
        const summary = summarize(decision);
        res.setHeader("x-frugal-model", summary.model);
        if (summary.effort) res.setHeader("x-frugal-effort", summary.effort);
        res.setHeader("x-frugal-decision", JSON.stringify({ ...summary, rationale: undefined }));

        if (body.stream) {
          res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache", connection: "keep-alive" });
          for await (const chunk of frugal.stream(body, decision)) res.write(`data: ${JSON.stringify(chunk)}\n\n`);
          res.write("data: [DONE]\n\n");
          return res.end();
        }
        const { response } = await frugal.complete(body, decision);
        return json(res, 200, response);
      }
      return json(res, 404, { error: { message: `no route for ${req.method} ${url.pathname}`, type: "not_found" } });
    } catch (err) {
      if (res.headersSent) {
        res.write(`data: ${JSON.stringify({ error: { message: errMessage(err) } })}\n\n`);
        return res.end();
      }
      const status = err instanceof UpstreamError && err.status >= 400 ? err.status : 500;
      return json(res, status, { error: { message: errMessage(err), type: status === 500 ? "server_error" : "upstream_error" } });
    }
  });
}

export function startServer(frugal: Frugal, port = Number(process.env[ENV.port] ?? 8787)) {
  const server = createFrugalServer(frugal);
  server.listen(port, () => {
    const avail = frugal.available();
    console.log(`frugal listening on http://localhost:${port}`);
    console.log(`  jev routing : ${frugal.judge.enabled ? "enabled" : "DISABLED (no TYPESAFE_API_KEY; conservative fallback)"}`);
    console.log(`  providers   : ${(["anthropic", "openai", "openrouter"] as const).filter((p) => avail.some((m) => m.provider === p)).join(", ") || "none"}`);
    console.log(`  models      : ${avail.length} available of ${frugal.catalog.length} in catalog`);
    console.log(`  auth        : ${has(ENV.apiKey) ? "FRUGAL_API_KEY required" : "open (set FRUGAL_API_KEY to protect)"}`);
  });
  return server;
}

function authorized(req: IncomingMessage): boolean {
  const required = process.env[ENV.apiKey];
  if (!required) return true;
  const header = req.headers.authorization ?? "";
  return header === `Bearer ${required}` || req.headers["x-api-key"] === required;
}

function errMessage(err: unknown): string {
  // Never echo secrets: upstream messages are already truncated; strip anything that looks like a key.
  const m = err instanceof Error ? err.message : String(err);
  return m.replace(/(sk|key)[-_][A-Za-z0-9_-]{8,}/g, "[redacted]");
}

function cors(res: ServerResponse) {
  res.setHeader("access-control-allow-origin", "*");
  res.setHeader("access-control-allow-headers", "authorization, content-type, x-api-key");
  res.setHeader("access-control-allow-methods", "GET, POST, OPTIONS");
}

function json(res: ServerResponse, status: number, body: unknown) {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(body));
}

function end(res: ServerResponse, status: number) {
  res.writeHead(status);
  res.end();
}

function readJson(req: IncomingMessage): Promise<unknown> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    let size = 0;
    req.on("data", (c: Buffer) => {
      size += c.length;
      if (size > MAX_BODY) {
        reject(new Error("request body too large"));
        req.destroy();
        return;
      }
      chunks.push(c);
    });
    req.on("end", () => {
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}"));
      } catch {
        reject(new Error("invalid JSON body"));
      }
    });
    req.on("error", reject);
  });
}
