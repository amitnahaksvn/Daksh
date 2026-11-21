import http from "node:http";
import express, { type NextFunction, type Request, type Response } from "express";
import { adapters, NotSignedInError, VerificationError } from "./adapters/index.js";
import { config } from "./config.js";
import * as sessions from "./sessions.js";
import { attachStream } from "./stream.js";

const app = express();
app.use(express.json({ limit: "100kb" }));

app.get("/health", (_req, res) => res.json({ status: "ok", stores: Object.keys(adapters), remoteSignIn: config.remoteSignIn }));

// Everything else is internal-only: the .NET API is the sole caller, authenticated by a shared secret never exposed to the browser.
app.use((req: Request, res: Response, next: NextFunction) => {
  if (req.header("X-Internal-Key") !== config.internalServiceKey) { res.status(401).json({ error: "Unauthorized" }); return; }
  next();
});

const ID = /^[A-Za-z0-9_-]{1,64}$/;
function params(req: Request, res: Response): { userId: string; store: string } | null {
  const userId = String(req.params.userId ?? ""), store = String(req.params.store ?? "");
  if (!ID.test(userId) || !adapters[store]) { res.status(404).json({ error: "Unknown store." }); return null; }
  return { userId, store };
}
function fail(res: Response, err: unknown): void {
  if (err instanceof NotSignedInError) { res.status(409).json({ error: err.message, code: "not_signed_in" }); return; }
  if (err instanceof VerificationError) { res.status(409).json({ error: err.message, code: "verification" }); return; }
  res.status(500).json({ error: err instanceof Error ? err.message : "Something went wrong." });
}

app.get("/sessions/:userId/:store", async (req, res) => {
  const p = params(req, res); if (!p) return;
  res.json({ state: await sessions.getState(p.userId, p.store), remote: config.remoteSignIn });
});

app.post("/sessions/:userId/:store/start", async (req, res) => {
  const p = params(req, res); if (!p) return;
  try { res.json({ state: await sessions.startSignIn(p.userId, p.store), remote: config.remoteSignIn }); } catch (err) { fail(res, err); }
});

app.delete("/sessions/:userId/:store", async (req, res) => {
  const p = params(req, res); if (!p) return;
  try { await sessions.signOut(p.userId, p.store); res.status(204).end(); } catch (err) { fail(res, err); }
});

app.post("/cart/:userId/:store/add", async (req, res) => {
  const p = params(req, res); if (!p) return;
  const items = Array.isArray(req.body?.items) ? (req.body.items as unknown[]).filter((i): i is string => typeof i === "string").map((i) => i.trim()).filter(Boolean) : [];
  if (items.length === 0) { res.status(400).json({ error: "Say what to add." }); return; }
  if (items.length > config.maxItems) { res.status(400).json({ error: `Add at most ${config.maxItems} items at a time.` }); return; }
  if (items.some((i) => i.length > 120)) { res.status(400).json({ error: "An item name is too long." }); return; }
  try { res.json(await sessions.addToCart(p.userId, p.store, items)); } catch (err) { fail(res, err); }
});

app.get("/cart/:userId/:store", async (req, res) => {
  const p = params(req, res); if (!p) return;
  try { res.json(await sessions.readCart(p.userId, p.store)); } catch (err) { fail(res, err); }
});

const server = http.createServer(app);
attachStream(server); // the live sign-in view (cloud mode); it has its own signed-token check, separate from the internal key above
server.listen(config.port, config.host, () => console.log(`Shopping service listening on ${config.host}:${config.port}${config.remoteSignIn ? " (remote sign-in)" : ""}`));
