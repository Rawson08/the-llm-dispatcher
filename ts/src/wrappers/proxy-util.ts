import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { Readable } from "node:stream";

/**
 * Loopback HTTP helpers shared by the Claude Code and Codex wrappers.
 *
 * The proxy binds to 127.0.0.1 only, forwards the CLI's own request headers to the upstream
 * unchanged (authorization included), and never reads, logs, or stores credential values.
 */

export type LoopbackHandler = (req: IncomingMessage, res: ServerResponse, body: Buffer) => Promise<void>;

export function startLoopback(handler: LoopbackHandler): Promise<{ port: number; server: Server; close: () => void }> {
  return new Promise((resolve, reject) => {
    const server = createServer(async (req, res) => {
      try {
        const body = await readBody(req);
        await handler(req, res, body);
      } catch (err) {
        if (!res.headersSent) {
          res.writeHead(502, { "content-type": "application/json" });
          res.end(JSON.stringify({ type: "error", error: { type: "dispatcher_proxy_error", message: redact(err) } }));
        } else {
          res.end();
        }
      }
    });
    server.keepAliveTimeout = 65_000;
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const addr = server.address();
      const port = typeof addr === "object" && addr ? addr.port : 0;
      resolve({ port, server, close: () => server.close() });
    });
  });
}

export function readBody(req: IncomingMessage): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on("data", (c: Buffer) => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", reject);
  });
}

/** Hop-by-hop or transport headers that must not be copied between connections. */
const DROP_REQUEST_HEADERS = new Set(["host", "content-length", "connection", "transfer-encoding", "accept-encoding", "keep-alive"]);
const DROP_RESPONSE_HEADERS = new Set(["content-length", "content-encoding", "transfer-encoding", "connection", "keep-alive"]);

/**
 * Forward a request to `upstreamUrl` and stream the response back verbatim. Compression is
 * disabled on the upstream leg so SSE pings and deltas arrive as they are produced.
 */
export async function forward(upstreamUrl: string, req: IncomingMessage, body: Buffer | undefined, res: ServerResponse): Promise<void> {
  const headers: Record<string, string> = {};
  for (const [k, v] of Object.entries(req.headers)) {
    if (v === undefined || DROP_REQUEST_HEADERS.has(k)) continue;
    headers[k] = Array.isArray(v) ? v.join(", ") : v;
  }
  headers["accept-encoding"] = "identity";
  const method = req.method ?? "GET";
  const upstream = await fetch(upstreamUrl, {
    method,
    headers,
    body: method === "GET" || method === "HEAD" || !body?.length ? undefined : new Uint8Array(body),
    redirect: "manual",
  });
  const outHeaders: Record<string, string> = {};
  upstream.headers.forEach((v, k) => {
    if (!DROP_RESPONSE_HEADERS.has(k.toLowerCase())) outHeaders[k] = v;
  });
  res.writeHead(upstream.status, outHeaders);
  if (!upstream.body || method === "HEAD") return void res.end();
  await new Promise<void>((resolve) => {
    const stream = Readable.fromWeb(upstream.body as import("node:stream/web").ReadableStream);
    stream.on("error", () => res.end());
    res.on("close", () => stream.destroy());
    stream.pipe(res).on("finish", resolve).on("close", resolve);
  });
}

export function redact(err: unknown): string {
  const m = err instanceof Error ? err.message : String(err);
  return m.replace(/(sk|key|bearer)[-_ ][A-Za-z0-9_.-]{8,}/gi, "[redacted]");
}
