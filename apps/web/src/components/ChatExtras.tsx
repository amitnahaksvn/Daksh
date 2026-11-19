"use client";
import { useState } from "react";
import { api, ApiError, type ChatAssistResult } from "@/lib/api";

const EMOJIS = ["😀","😂","🥹","😍","🥰","😘","😊","😉","😎","🤔","😅","😭","😢","😡","🙏","👍","👎","👏","🙌","💪","🔥","🎉","❤️","💔","✅","❌","👀","🤝","🙂","😴","🤗","😇","🥳","😬","🤷","💯","🌸","☕","🍕","🎂"];

/** A small emoji palette that inserts into the message box. */
export function EmojiButton({ onPick, disabled = false }: { onPick: (emoji: string) => void; disabled?: boolean }) {
  const [open, setOpen] = useState(false);
  return (
    <span className="relative">
      <button type="button" disabled={disabled} onClick={() => setOpen((v) => !v)} aria-label="Insert an emoji" aria-expanded={open} title="Emoji" className="rounded-full border border-line bg-surface px-3 py-2 text-sm hover:bg-surface-2 disabled:opacity-50">😊</button>
      {open && (<>
        <button type="button" aria-label="Close emoji picker" onClick={() => setOpen(false)} className="fixed inset-0 z-10 cursor-default" />
        <div role="dialog" aria-label="Emoji" className="absolute bottom-full left-0 z-20 mb-2 grid w-64 grid-cols-8 gap-1 rounded-xl border border-line bg-surface p-2 shadow-[var(--shadow)]">
          {EMOJIS.map((e) => <button key={e} type="button" onClick={() => { onPick(e); setOpen(false); }} className="rounded-md p-1 text-lg hover:bg-surface-2">{e}</button>)}
        </div>
      </>)}
    </span>
  );
}

/** "Today", "Yesterday", or a date, for the dividers between days in a thread. */
export function dayLabel(iso: string, now = new Date()): string {
  const d = new Date(iso);
  const start = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((start(now) - start(d)) / 86400000);
  if (diff === 0) return "Today";
  if (diff === 1) return "Yesterday";
  return d.toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "long", year: d.getFullYear() === now.getFullYear() ? undefined : "numeric" });
}
export const sameDay = (a: string, b: string) => new Date(a).toDateString() === new Date(b).toDateString();

export function DayDivider({ iso }: { iso: string }) {
  return <div role="separator" className="my-1 flex items-center gap-3 text-[11px] font-semibold text-muted"><span className="h-px flex-1 bg-line" /><span>{dayLabel(iso)}</span><span className="h-px flex-1 bg-line" /></div>;
}

/** Saves the visible messages as a plain text file. */
export function exportChat(name: string, messages: { fromMe: boolean; text: string; timestamp: string; senderName?: string | null; chatName?: string | null }[]) {
  const lines = messages.map((m) => `[${new Date(m.timestamp).toLocaleString()}] ${m.fromMe ? "Me" : (m.senderName ?? m.chatName ?? name)}: ${m.text}`);
  const blob = new Blob([lines.join("\n")], { type: "text/plain;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url; a.download = `${name.replace(/[^\p{L}\p{N}]+/gu, "_") || "chat"}.txt`; a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** AI help for the open conversation, only on request: a summary, or three suggested replies to pick from (nothing is sent by itself). */
export function AssistBar({ channel, connectionId, chatId, onUse }: { channel: "whatsapp" | "telegram"; connectionId: string; chatId: string; onUse: (text: string) => void }) {
  const [busy, setBusy] = useState<"summary" | "suggest" | null>(null);
  const [result, setResult] = useState<ChatAssistResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  async function ask(mode: "summary" | "suggest") {
    setBusy(mode); setError(null); setResult(null);
    try { setResult(await api.chatAssist(channel, connectionId, chatId, mode)); }
    catch (e) { setError(e instanceof ApiError ? e.message : "The AI could not help right now."); }
    finally { setBusy(null); }
  }
  const btn = "rounded-full border border-line px-3 py-1 text-xs font-semibold text-muted hover:bg-surface-2 hover:text-text disabled:opacity-50";
  return (
    <div className="border-t border-line px-3 py-2">
      <div className="flex flex-wrap items-center gap-2">
        <button type="button" onClick={() => void ask("summary")} disabled={busy !== null} className={btn}>{busy === "summary" ? "Summarizing…" : "✨ Summarize"}</button>
        <button type="button" onClick={() => void ask("suggest")} disabled={busy !== null} className={btn}>{busy === "suggest" ? "Thinking…" : "✨ Suggest replies"}</button>
        {(result || error) && <button type="button" onClick={() => { setResult(null); setError(null); }} aria-label="Dismiss" className="ml-auto text-xs text-muted hover:text-text">✕</button>}
      </div>
      {error && <p role="alert" className="mt-2 text-xs text-danger">{error}</p>}
      {result?.mode === "summary" && result.text && <p role="status" className="mt-2 whitespace-pre-wrap rounded-xl bg-surface-2 p-3 text-xs">{result.text}</p>}
      {result?.mode === "suggest" && (
        <div className="mt-2 flex flex-col gap-1.5" role="list" aria-label="Suggested replies">
          {result.suggestions.map((s) => <button key={s} role="listitem" type="button" onClick={() => { onUse(s); setResult(null); }} className="rounded-xl bg-surface-2 px-3 py-2 text-left text-xs hover:bg-line">{s}</button>)}
          <p className="text-[10px] text-muted">Pick one to put it in the message box. Nothing is sent until you press Send.</p>
        </div>
      )}
    </div>
  );
}
