"use client";
import { useEffect, useState } from "react";
import { api, ApiError } from "@/lib/api";
import { Orb } from "./Orb";
import { buttonClass, inputClass, tileClass } from "./ui";

type Line = { role: "user" | "assistant"; text: string; simulated?: boolean; typed?: boolean };

const SUGGESTIONS = ["Plan my day", "What is on my task list?", "Remind me to call mum tomorrow", "Summarise what I did this week"];

/** Types the reply out. Screen readers get the whole reply at once; people who prefer less motion (and tests) see it instantly. */
function Reply({ text, animate }: { text: string; animate: boolean }) {
  const [shown, setShown] = useState(animate ? 0 : text.length);
  useEffect(() => {
    if (!animate) return;
    let n = 0;
    const id = window.setInterval(() => {
      n = Math.min(n + 2, text.length);
      setShown(n);
      if (n >= text.length) window.clearInterval(id);
    }, 16);
    return () => window.clearInterval(id);
  }, [text, animate]);

  if (!animate) return <>{text}</>;
  return (
    <>
      <span aria-hidden="true" className={shown < text.length ? "type-caret" : ""}>{text.slice(0, shown)}</span>
      <span className="sr-only">{text}</span>
    </>
  );
}

export function AssistantChat() {
  const [lines, setLines] = useState<Line[]>([]);
  const [text, setText] = useState("");
  const [conversationId, setConversationId] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const message = text.trim();
    if (!message || busy) return;
    setBusy(true);
    setError(undefined);
    setLines((l) => [...l, { role: "user", text: message }]);
    setText("");
    try {
      const r = await api.sendMessage(message, conversationId);
      setConversationId(r.conversationId);
      const motionOk = typeof window.matchMedia === "function" && !window.matchMedia("(prefers-reduced-motion: reduce)").matches;
      setLines((l) => [...l, { role: "assistant", text: r.reply, simulated: r.isSimulated, typed: motionOk }]);
    } catch (err) {
      setText(message);
      setLines((l) => l.slice(0, -1));
      setError(err instanceof ApiError ? err.message : "Unexpected error.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-label="Conversation" className={`flex flex-col gap-4 p-4 ${tileClass}`}>
      <ol aria-live="polite" className="flex min-h-64 flex-col gap-3">
        {lines.length === 0 && !busy && (
          <li className="flex flex-1 flex-col items-center justify-center gap-4 py-6 text-center">
            <Orb size={64} />
            <p className="text-lg font-bold">What can I do for you?</p>
            <ul className="flex max-w-lg flex-wrap justify-center gap-2">
              {SUGGESTIONS.map((s) => (
                <li key={s}>
                  <button type="button" onClick={() => setText(s)} className="rounded-full border border-line bg-surface-2 px-3.5 py-1.5 text-sm text-muted transition-colors hover:text-text">
                    {s}
                  </button>
                </li>
              ))}
            </ul>
          </li>
        )}
        {lines.map((l, i) =>
          l.role === "user" ? (
            <li key={i} className="max-w-[85%] self-end rounded-2xl rounded-br-md bg-accent px-4 py-2.5 text-sm text-accent-contrast">
              <span className="sr-only">You: </span>
              {l.text}
            </li>
          ) : (
            <li key={i} className="flex max-w-[92%] items-start gap-2.5 self-start">
              <Orb size={26} className="mt-1" />
              <div className="ai-reply rounded-2xl rounded-tl-md bg-surface-2 px-4 py-2.5 text-sm">
                <span className="sr-only">Daksh: </span>
                <Reply text={l.text} animate={!!l.typed} />
                {l.simulated && <span className="ai-mono mt-1.5 block text-xs italic text-muted">(simulated reply)</span>}
              </div>
            </li>
          ),
        )}
        {busy && (
          <li role="status" className="flex items-center gap-2.5 self-start text-sm text-muted">
            <Orb size={26} thinking />
            <span className="think-dots" aria-hidden="true"><span /><span /><span /></span>
            <span className="sr-only">Daksh is thinking</span>
          </li>
        )}
      </ol>
      {error && <p role="alert" className="text-sm font-medium text-danger">{error}</p>}
      <form onSubmit={submit} className="flex gap-2">
        <label htmlFor="message" className="sr-only">Message</label>
        <div className="ai-input-ring min-w-0 flex-1">
          <input id="message" className={inputClass} value={text} onChange={(e) => setText(e.target.value)} placeholder="Ask Daksh…" maxLength={4000} />
        </div>
        <button className={buttonClass} disabled={busy}>{busy ? "Sending" : "Send"}</button>
      </form>
    </section>
  );
}
