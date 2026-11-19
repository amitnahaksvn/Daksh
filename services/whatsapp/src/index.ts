import "./loadEnv.js";
import express, { type NextFunction, type Request, type Response } from "express";
import { config } from "./config.js";
import { getDb } from "./db.js";
import * as sessions from "./sessionManager.js";

const app = express();
app.use(express.json({ limit: "30mb" })); // attachments arrive base64-encoded

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

app.post("/sessions/:connectionId/send", async (req, res) => {
  const { chatId, text, quotedMessageId, isAiGenerated, signature } = req.body as {
    signature?: string | null;
    chatId?: string;
    text?: string;
    quotedMessageId?: string;
    isAiGenerated?: boolean;
  };
  if (!chatId || !text) {
    res.status(400).json({ error: "chatId and text are required" });
    return;
  }
  try {
    const db = await getDb();
    await sessions.sendMessage(db, req.params.connectionId, chatId, text, quotedMessageId, isAiGenerated ?? false, signature ?? undefined);
    res.json({ status: "sent" });
  } catch (err) {
    res.status(409).json({ error: err instanceof Error ? err.message : "Could not send the message." });
  }
});

app.post("/sessions/:connectionId/react", async (req, res) => {
  const { chatId, messageId, emoji } = req.body as { chatId?: string; messageId?: string; emoji?: string };
  if (!chatId || !messageId || emoji === undefined) {
    res.status(400).json({ error: "chatId, messageId and emoji are required" });
    return;
  }
  try {
    const db = await getDb();
    await sessions.sendReaction(db, req.params.connectionId, chatId, messageId, emoji);
    res.json({ status: "ok" });
  } catch (err) {
    res.status(409).json({ error: err instanceof Error ? err.message : "Could not react to the message." });
  }
});


type Body = Record<string, unknown>;
const str = (v: unknown) => (typeof v === "string" ? v : undefined);
/** One place for the "do it, or say why not" shape every action route shares. */
const action = (fn: (db: Awaited<ReturnType<typeof getDb>>, id: string, body: Body, req: express.Request) => Promise<unknown>) =>
  async (req: express.Request, res: express.Response) => {
    try {
      res.json((await fn(await getDb(), req.params.connectionId as string, (req.body ?? {}) as Body, req)) ?? { status: "ok" });
    } catch (err) {
      res.status(409).json({ error: err instanceof Error ? err.message : "That did not work." });
    }
  };

app.post("/sessions/:connectionId/send-media", action(async (db, id, b) => {
  const kind = str(b.kind) as "image" | "sticker" | "video" | "audio" | "document" | undefined;
  const chatId = str(b.chatId); const data = str(b.data);
  if (!chatId || !data || !kind || !["image", "sticker", "video", "audio", "document"].includes(kind)) throw new Error("chatId, kind and data are required.");
  await sessions.sendMedia(db, id, chatId, { kind, data: Buffer.from(data, "base64"), mimeType: str(b.mimeType) ?? "application/octet-stream", fileName: str(b.fileName), caption: str(b.caption), quotedMessageId: str(b.quotedMessageId), voiceNote: b.voiceNote === true });
}));
app.post("/sessions/:connectionId/read", action(async (db, id, b) => {
  const chatId = str(b.chatId); if (!chatId) throw new Error("chatId is required.");
  return { marked: await sessions.markChatRead(db, id, chatId) };
}));
app.post("/sessions/:connectionId/delete", action(async (db, id, b) => {
  const chatId = str(b.chatId); const messageId = str(b.messageId);
  if (!chatId || !messageId) throw new Error("chatId and messageId are required.");
  await sessions.deleteMessage(db, id, chatId, messageId, b.forEveryone === true);
}));
app.post("/sessions/:connectionId/edit", action(async (db, id, b) => {
  const chatId = str(b.chatId); const messageId = str(b.messageId); const text = str(b.text);
  if (!chatId || !messageId || text === undefined) throw new Error("chatId, messageId and text are required.");
  await sessions.editMessage(db, id, chatId, messageId, text);
}));
app.post("/sessions/:connectionId/forward", action(async (db, id, b) => {
  const toChatId = str(b.toChatId); const messageId = str(b.messageId);
  if (!toChatId || !messageId) throw new Error("toChatId and messageId are required.");
  await sessions.forwardMessage(db, id, toChatId, messageId);
}));
app.post("/sessions/:connectionId/presence", action(async (_db, id, b) => {
  const chatId = str(b.chatId); const state = str(b.state);
  if (!chatId || (state !== "composing" && state !== "paused" && state !== "available")) throw new Error("chatId and a valid state are required.");
  await sessions.setPresence(id, chatId, state);
}));
app.post("/sessions/:connectionId/group-action", action(async (_db, id, b) => {
  const chatId = str(b.chatId); const act = str(b.action);
  if (!chatId || !act) throw new Error("chatId and action are required.");
  if (!["rename", "description", "invite", "leave", "add", "remove", "promote", "demote"].includes(act)) throw new Error("Unknown group action.");
  return sessions.groupAction(id, chatId, act as sessions.GroupAction, { text: typeof b.text === "string" ? b.text : undefined, participants: Array.isArray(b.participants) ? b.participants.filter((p): p is string => typeof p === "string") : undefined });
}));
app.post("/sessions/:connectionId/disappearing", action(async (_db, id, b) => {
  const chatId = str(b.chatId); const seconds = typeof b.seconds === "number" ? b.seconds : NaN;
  if (!chatId || ![0, 86400, 604800, 7776000].includes(seconds)) throw new Error("chatId and a valid duration are required.");
  await sessions.setDisappearing(id, chatId, seconds);
}));
app.post("/sessions/:connectionId/block", action(async (_db, id, b) => {
  const chatId = str(b.chatId); if (!chatId) throw new Error("chatId is required.");
  await sessions.setBlocked(id, chatId, b.blocked !== false);
}));
app.get("/sessions/:connectionId/presence", async (req, res) => {
  const chatId = str(req.query.chatId);
  if (!chatId) { res.status(400).json({ error: "chatId is required" }); return; }
  res.json(await sessions.getPresence(req.params.connectionId, chatId));
});
app.get("/sessions/:connectionId/group", async (req, res) => {
  const chatId = str(req.query.chatId);
  if (!chatId) { res.status(400).json({ error: "chatId is required" }); return; }
  try { res.json(await sessions.groupInfo(req.params.connectionId, chatId)); }
  catch (err) { res.status(409).json({ error: err instanceof Error ? err.message : "Could not load the group." }); }
});
app.get("/sessions/:connectionId/profile-picture", async (req, res) => {
  const chatId = str(req.query.chatId);
  if (!chatId) { res.status(400).json({ error: "chatId is required" }); return; }
  const pic = await sessions.profilePicture(await getDb(), req.params.connectionId, chatId);
  if (!pic) { res.status(404).json({ error: "No profile picture" }); return; }
  res.set("Content-Type", pic.mimeType).set("Cache-Control", "private, max-age=3600").send(pic.data);
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
