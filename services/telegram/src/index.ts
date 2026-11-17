import "./loadEnv.js";
import express, { type NextFunction, type Request, type Response } from "express";
import { config, telegramConfigured } from "./config.js";
import { getDb } from "./db.js";
import * as sessions from "./sessionManager.js";

const app = express();
app.use(express.json());

app.get("/health", (_req, res) => res.json({ status: "ok", configured: telegramConfigured }));

// Every other route is internal-only: the .NET API is the sole caller, authenticated by a shared secret.
function requireInternalKey(req: Request, res: Response, next: NextFunction): void {
  if (req.header("X-Internal-Key") !== config.internalServiceKey) {
    res.status(401).json({ error: "Unauthorized" });
    return;
  }
  next();
}
app.use(requireInternalKey);

app.post("/sessions/:id/connect", async (req, res) => {
  await sessions.connect(await getDb(), req.params.id);
  res.json(sessions.getState(req.params.id));
});
app.get("/sessions/:id/state", (req, res) => res.json(sessions.getState(req.params.id)));
app.post("/sessions/:id/password", (req, res) => {
  const { password } = req.body as { password?: string };
  if (!password) { res.status(400).json({ error: "password is required" }); return; }
  res.json({ accepted: sessions.submitPassword(req.params.id, password) });
});
app.post("/sessions/:id/reconnect", async (req, res) => {
  await sessions.reconnect(await getDb(), req.params.id);
  res.json(sessions.getState(req.params.id));
});
app.post("/sessions/:id/disconnect", async (req, res) => {
  await sessions.disconnect(await getDb(), req.params.id);
  res.json({ status: "Disconnected" });
});
app.post("/sessions/:id/send", async (req, res) => {
  const { chatId, text } = req.body as { chatId?: string; text?: string };
  if (!chatId || !text) { res.status(400).json({ error: "chatId and text are required" }); return; }
  try {
    res.json(await sessions.sendMessage(req.params.id, chatId, text));
  } catch (err) {
    res.status(409).json({ error: err instanceof Error ? err.message : "Could not send the message." });
  }
});

async function main(): Promise<void> {
  await sessions.restoreSessionsFromDatabase(await getDb());
  app.listen(config.port, () => console.log(`Telegram integration service listening on :${config.port}`));
}

main().catch((err) => {
  console.error("Fatal startup error", err);
  process.exit(1);
});
