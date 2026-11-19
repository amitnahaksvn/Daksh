"use client";
import { useEffect, useRef, useState } from "react";
import { api, ApiError } from "@/lib/api";
import { announceChatsChanged, OPEN_CHAT_EVENT, takePendingChat } from "@/lib/dashboardTabsState";
import { Markdown } from "./Markdown";
import { Orb } from "./Orb";
import { buttonClass, tileClass } from "./ui";

type Line = { role: "user" | "assistant"; text: string; simulated?: boolean; typed?: boolean; sources?: string[] };

const SUGGESTIONS = ["Catch me up on today", "What's unread on WhatsApp?", "Any new emails in my inbox?", "What tasks are open?", "Summarise my latest chats"];

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

  if (!animate) return <Markdown text={text} />;
  return (
    <>
      <div aria-hidden="true" className={shown < text.length ? "type-caret" : ""}><Markdown text={text.slice(0, shown)} /></div>
      <div className="sr-only">{text}</div>
    </>
  );
}

/** `tall` gives the conversation room to breathe (the dashboard's Ask tab); the default is the compact version. */
export function AssistantChat({ tall = false }: { tall?: boolean }) {
  const [lines, setLines] = useState<Line[]>([]);
  const [text, setText] = useState("");
  const [conversationId, setConversationId] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  // The owner has not yet allowed the AI provider to see their data: hold the question and ask once.
  const [consent, setConsent] = useState(false);
  const box = useRef<HTMLTextAreaElement>(null);
  const endRef = useRef<HTMLLIElement>(null);

  // The sidebar's chat history opens a past chat here, or starts a fresh one (no id).
  useEffect(() => {
    const open = (id: string | null) => {
      setError(undefined); setConsent(false); setText("");
      if (!id) { setConversationId(undefined); setLines([]); return; }
      void api.messages(id).then(
        (all) => { setConversationId(id); setLines(all.map((m) => ({ role: m.role, text: m.content }))); },
        () => setError("Could not open that chat."),
      );
    };
    const onOpen = (e: Event) => { takePendingChat(); open((e as CustomEvent<string | null>).detail ?? null); };
    window.addEventListener(OPEN_CHAT_EVENT, onOpen);
    const pending = takePendingChat();
    if (pending !== undefined) queueMicrotask(() => open(pending));
    return () => window.removeEventListener(OPEN_CHAT_EVENT, onOpen);
  }, []);
  // The box grows with what is typed (up to a few lines), like a chat app's.
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
  }, [text]);
  // Keep the newest message in view.
  useEffect(() => { endRef.current?.scrollIntoView?.({ block: "end" }); }, [lines, busy]);

  async function send() {
    const message = text.trim();
    if (!message || busy) return;
    setBusy(true);
    setError(undefined);
    setLines((l) => [...l, { role: "user", text: message }]);
    setText("");
    try {
      const r = await api.sendMessage(message, conversationId);
      if (r.conversationId !== conversationId) announceChatsChanged(); // a new chat: it joins the sidebar's history
      setConversationId(r.conversationId);
      const motionOk = typeof window.matchMedia === "function" && !window.matchMedia("(prefers-reduced-motion: reduce)").matches;
      setLines((l) => [...l, { role: "assistant", text: r.reply, simulated: r.isSimulated, typed: motionOk, sources: r.sources }]);
    } catch (err) {
      setText(message);
      setLines((l) => l.slice(0, -1));
      if (err instanceof ApiError && err.code === "ai_consent_required") setConsent(true);
      else setError(err instanceof ApiError ? err.message : "Unexpected error.");
    } finally {
      setBusy(false);
    }
  }
  const submit = (e: React.FormEvent) => { e.preventDefault(); void send(); };

  /** One-time permission: let Daksha's AI provider see the question and the app data it needs, then send the held question. */
  async function allowAndSend() {
    setBusy(true); setError(undefined);
    try {
      const current = await api.privacy().catch(() => ({ memoryEnabled: false, allowProviderDataSharing: false, retentionDays: 365 }));
      await api.updatePrivacy({ ...current, allowProviderDataSharing: true });
      setConsent(false);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not save that choice.");
      setBusy(false);
      return;
    }
    setBusy(false);
    await send();
  }

  return (
    <section aria-label="Conversation" className={`flex flex-col gap-4 p-4 ${tall ? "min-h-[calc(100dvh-11rem)] md:min-h-[calc(100dvh-9.5rem)]" : ""} ${tileClass}`}>
      <ol aria-live="polite" className={`flex flex-1 flex-col gap-3 ${tall ? "min-h-[16rem]" : "min-h-64"}`}>
        {lines.length === 0 && !busy && (
          <li className={`flex flex-1 flex-col items-center justify-center gap-4 text-center ${tall ? "py-6" : "py-6"}`}>
            <Orb size={tall ? 88 : 64} />
            <p className={`font-bold ${tall ? "text-3xl" : "text-lg"}`}>What can I do for you?</p>
            <ul className={`flex flex-wrap justify-center gap-2 ${tall ? "max-w-3xl" : "max-w-lg"}`}>
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
                {!!l.sources?.length && <span className="ai-mono mt-1.5 block text-xs text-muted">Checked: {l.sources.join(", ")}</span>}
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
        <li ref={endRef} aria-hidden className="h-0" />
      </ol>
      {consent && (
        <div role="alert" className="rounded-2xl border border-line bg-surface-2 p-4 text-sm">
          <p className="font-semibold">Allow Daksha to use its AI provider?</p>
          <p className="mt-1 text-muted">To answer, your question and any app data it needs (for example the chat or the mail you ask about) are sent to the AI provider. Daksha only reads; it never sends or changes anything from here. You can turn this off later in your privacy settings.</p>
          <div className="mt-3 flex gap-2">
            <button type="button" onClick={() => void allowAndSend()} disabled={busy} className={buttonClass}>Allow and continue</button>
            <button type="button" onClick={() => setConsent(false)} className="rounded-full border border-line px-4 py-2 text-sm font-semibold hover:bg-surface">Not now</button>
          </div>
        </div>
      )}
      {error && <p role="alert" className="text-sm font-medium text-danger">{error}</p>}
      <form onSubmit={submit} className="flex items-end gap-2 rounded-2xl border border-line bg-surface-2 p-2">
        <label htmlFor="message" className="sr-only">Message</label>
        <textarea
          id="message" ref={box} rows={1} value={text} onChange={(e) => setText(e.target.value)} maxLength={4000}
          onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); void send(); } }}
          placeholder="Ask Daksha anything…  (Shift+Enter for a new line)"
          className="field-in-pill max-h-[200px] min-w-0 flex-1 resize-none bg-transparent px-2 py-2 text-sm text-text placeholder:text-muted"
        />
        <button className={`${buttonClass} shrink-0`} disabled={busy}>{busy ? "Sending" : "Send"}</button>
      </form>
    </section>
  );
}
