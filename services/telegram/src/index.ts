import "./loadEnv.js";
import express, { type NextFunction, type Request, type Response } from "express";
import { config, telegramConfigured } from "./config.js";
import { getDb } from "./db.js";
import * as sessions from "./sessionManager.js";

const app = express();
app.use(express.json({ limit: "30mb" })); // attachments arrive base64-encoded

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
  const { chatId, text, replyToId } = req.body as { chatId?: string; text?: string; replyToId?: string };
  if (!chatId || !text) { res.status(400).json({ error: "chatId and text are required" }); return; }
  try {
    res.json(await sessions.sendMessage(req.params.id, chatId, text, replyToId));
  } catch (err) {
    res.status(409).json({ error: err instanceof Error ? err.message : "Could not send the message." });
  }
});

app.get("/sessions/:id/profile-picture", async (req, res) => {
  const chatId = typeof req.query.chatId === "string" ? req.query.chatId : undefined;
  if (!chatId) { res.status(400).json({ error: "chatId is required" }); return; }
  const data = await sessions.profilePicture(await getDb(), req.params.id, chatId);
  if (!data) { res.status(404).json({ error: "No profile picture" }); return; }
  res.set("Content-Type", "image/jpeg").set("Cache-Control", "private, max-age=3600").send(data);
});


type Body = Record<string, unknown>;
const str = (v: unknown) => (typeof v === "string" && v ? v : undefined);
/** One place for the "do it, or say why not" shape every action route shares. */
const action = (fn: (db: Awaited<ReturnType<typeof getDb>>, id: string, body: Body) => Promise<unknown>) =>
  async (req: Request, res: Response) => {
    try {
      res.json((await fn(await getDb(), req.params.id as string, (req.body ?? {}) as Body)) ?? { status: "ok" });
    } catch (err) {
      res.status(409).json({ error: err instanceof Error ? err.message : "That did not work." });
    }
  };
const need = (b: Body, ...keys: string[]) => { for (const k of keys) if (str(b[k]) === undefined) throw new Error(`${keys.join(", ")} are required.`); };

app.post("/sessions/:id/send-media", action(async (db, id, b) => {
  need(b, "chatId", "data", "fileName");
  return sessions.sendMedia(db, id, str(b.chatId)!, { data: Buffer.from(str(b.data)!, "base64"), fileName: str(b.fileName)!, mimeType: str(b.mimeType) ?? "application/octet-stream", caption: str(b.caption), replyToId: str(b.replyToId), asDocument: b.asDocument === true });
}));
app.post("/sessions/:id/read", action(async (_db, id, b) => { need(b, "chatId"); await sessions.markRead(id, str(b.chatId)!); }));
app.post("/sessions/:id/delete", action(async (_db, id, b) => { need(b, "chatId", "messageId"); await sessions.deleteMessage(id, str(b.chatId)!, str(b.messageId)!, b.forEveryone === true); }));
app.post("/sessions/:id/edit", action(async (_db, id, b) => { need(b, "chatId", "messageId"); await sessions.editMessage(id, str(b.chatId)!, str(b.messageId)!, typeof b.text === "string" ? b.text : ""); }));
app.post("/sessions/:id/forward", action(async (_db, id, b) => { need(b, "fromChatId", "messageId", "toChatId"); return (await sessions.forwardMessage(id, str(b.fromChatId)!, str(b.messageId)!, str(b.toChatId)!)) ?? { status: "ok" }; }));
app.post("/sessions/:id/react", action(async (_db, id, b) => { need(b, "chatId", "messageId"); await sessions.react(id, str(b.chatId)!, str(b.messageId)!, typeof b.emoji === "string" ? b.emoji : ""); }));
app.post("/sessions/:id/typing", action(async (_db, id, b) => { need(b, "chatId"); await sessions.setTyping(id, str(b.chatId)!, b.typing === true); }));
app.post("/sessions/:id/history", action(async (db, id, b) => { need(b, "chatId"); return { fetched: await sessions.loadOlder(db, id, str(b.chatId)!, str(b.beforeId), typeof b.limit === "number" ? b.limit : 30) }; }));
app.get("/sessions/:id/presence", (req, res) => {
  const chatId = str(req.query.chatId);
  if (!chatId) { res.status(400).json({ error: "chatId is required" }); return; }
  res.json(sessions.presence(req.params.id, chatId));
});
app.get("/sessions/:id/group", async (req, res) => {
  const chatId = str(req.query.chatId);
  if (!chatId) { res.status(400).json({ error: "chatId is required" }); return; }
  try { res.json(await sessions.groupInfo(req.params.id, chatId)); }
  catch (err) { res.status(409).json({ error: err instanceof Error ? err.message : "Could not load the group." }); }
});

async function main(): Promise<void> {
  await sessions.restoreSessionsFromDatabase(await getDb());
  app.listen(config.port, () => console.log(`Telegram integration service listening on :${config.port}`));
}

main().catch((err) => {
  console.error("Fatal startup error", err);
  process.exit(1);
});
