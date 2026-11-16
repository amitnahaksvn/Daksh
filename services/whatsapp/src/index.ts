import "./loadEnv.js";
import express, { type NextFunction, type Request, type Response } from "express";
import { config } from "./config.js";
import { getDb } from "./db.js";
import * as sessions from "./sessionManager.js";

const app = express();
app.use(express.json());

app.get("/health", (_req, res) => res.json({ status: "ok" }));

// Every other route is internal-only: the .NET API is the sole caller, authenticated by a shared secret
// never exposed to the browser. This service is never reachable directly from the public internet.
function requireInternalKey(req: Request, res: Response, next: NextFunction): void {
  if (req.header("X-Internal-Key") !== config.internalServiceKey) {
    res.status(401).json({ error: "Unauthorized" });
    return;
  }
  next();
}
app.use(requireInternalKey);

app.post("/sessions/:connectionId/connect", async (req, res) => {
  const db = await getDb();
  await sessions.connect(db, req.params.connectionId);
  res.json({ status: sessions.getStatus(req.params.connectionId).status });
});

app.get("/sessions/:connectionId/qr", (req, res) => {
  const { qr, expiresAt, status } = sessions.getQr(req.params.connectionId);
  if (!qr) {
    res.status(404).json({ qr: null, expiresAt: null, status });
    return;
  }
  res.json({ qr, expiresAt: expiresAt?.toISOString() ?? null, status });
});

app.get("/sessions/:connectionId/status", (req, res) => {
  res.json(sessions.getStatus(req.params.connectionId));
});

app.post("/sessions/:connectionId/reconnect", async (req, res) => {
  const db = await getDb();
  await sessions.reconnect(db, req.params.connectionId);
  res.json({ status: sessions.getStatus(req.params.connectionId).status });
});

app.post("/sessions/:connectionId/disconnect", async (req, res) => {
  const db = await getDb();
  await sessions.disconnect(db, req.params.connectionId);
  res.json({ status: "Disconnected" });
});

async function main(): Promise<void> {
  const db = await getDb();
  await sessions.restoreSessionsFromDatabase(db);
  app.listen(config.port, () => console.log(`WhatsApp integration service listening on :${config.port}`));
}

main().catch((err) => {
  console.error("Fatal startup error", err);
  process.exit(1);
});
