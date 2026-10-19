"use client";
import { useState } from "react";
import { api, ApiError } from "@/lib/api";
import { buttonClass, inputClass } from "./ui";

type Line = { role: "user" | "assistant"; text: string; simulated?: boolean };

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
    try {
      const r = await api.sendMessage(message, conversationId);
      setConversationId(r.conversationId);
      setLines((l) => [...l, { role: "user", text: message }, { role: "assistant", text: r.reply, simulated: r.isSimulated }]);
      setText("");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Unexpected error.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-label="Conversation" className="flex flex-col gap-4">
      <ol aria-live="polite" className="flex min-h-48 flex-col gap-3">
        {lines.map((l, i) => (
          <li key={i} className={l.role === "user" ? "self-end rounded-lg bg-indigo-100 px-3 py-2 text-indigo-950" : "self-start rounded-lg bg-neutral-200 px-3 py-2 text-neutral-900"}>
            <span className="sr-only">{l.role === "user" ? "You: " : "Daksh: "}</span>
            {l.text}
            {l.simulated && <span className="ml-2 text-xs italic">(simulated reply)</span>}
          </li>
        ))}
      </ol>
      {error && <p role="alert" className="text-sm text-red-700">{error}</p>}
      <form onSubmit={submit} className="flex gap-2">
        <label htmlFor="message" className="sr-only">Message</label>
        <input id="message" className={inputClass} value={text} onChange={(e) => setText(e.target.value)} placeholder="Ask Daksh…" maxLength={4000} />
        <button className={buttonClass} disabled={busy}>{busy ? "Sending" : "Send"}</button>
      </form>
    </section>
  );
}
