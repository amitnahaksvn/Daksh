"use client";
import { useEffect, useState } from "react";
import { Dropdown, inputClass } from "@/components/ui";
import { chatDisplayName } from "@/components/WhatsAppChatsPanel";
import { Toggle } from "@/components/WhatsAppSettingsParts";
import { api, ApiError, type WhatsAppChat, type WhatsAppChatRule } from "@/lib/api";

const DEFAULT_RULE = (chatId: string): WhatsAppChatRule => ({ chatId, monitor: true, autoReply: "Default", priority: "Normal", aiExcluded: false, customReply: null });

/** Per-contact / per-group behaviour. Unlike the other tabs these save the moment you change them — each chat
 *  is its own record rather than part of the connection-wide settings document. */
export function WhatsAppContactsTab({ connectionId, chats, search }: { connectionId: string; chats: WhatsAppChat[] | null; search: string }) {
  const [rules, setRules] = useState<Record<string, WhatsAppChatRule> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [openChat, setOpenChat] = useState<string | null>(null);

  useEffect(() => {
    void api
      .whatsAppChatRules(connectionId)
      .then((list) => setRules(Object.fromEntries(list.map((r) => [r.chatId, r]))))
      .catch(() => setRules({}));
  }, [connectionId]);

  async function change(chatId: string, patch: Partial<Omit<WhatsAppChatRule, "chatId">>) {
    const previous = rules?.[chatId] ?? DEFAULT_RULE(chatId);
    setRules((r) => ({ ...(r ?? {}), [chatId]: { ...previous, ...patch } }));
    setError(null);
    try {
      const saved = await api.saveWhatsAppChatRule(connectionId, chatId, patch);
      setRules((r) => ({ ...(r ?? {}), [chatId]: saved }));
    } catch (err) {
      setRules((r) => ({ ...(r ?? {}), [chatId]: previous }));
      setError(err instanceof ApiError ? err.message : "Could not save that change.");
    }
  }

  if (!chats || !rules) return <p className="text-sm text-muted">Loading…</p>;
  if (chats.length === 0) return <p className="text-sm text-muted">No chats yet — contacts and groups appear here once messages have been exchanged.</p>;

  const visible = chats.filter((c) => !search || chatDisplayName(c).toLowerCase().includes(search.toLowerCase()));

  return (
    <div className="space-y-3">
      <p className="text-xs text-muted">
        Rules for individual contacts and groups. They override the connection-wide settings, and save immediately.
      </p>
      {error && <p role="alert" className="text-sm text-danger">{error}</p>}
      <ul className="space-y-2">
        {visible.map((c) => {
          const rule = rules[c.chatId] ?? DEFAULT_RULE(c.chatId);
          const open = openChat === c.chatId;
          return (
            <li key={c.chatId} className="rounded-[1.25rem] border border-line">
              <button onClick={() => setOpenChat(open ? null : c.chatId)} className="flex w-full items-center gap-2 p-3 text-left">
                <span className="min-w-0 flex-1 truncate text-sm font-semibold">{chatDisplayName(c)}</span>
                {c.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-2 py-0.5 text-[10px] font-semibold text-muted">GROUP</span>}
                {rule.priority === "High" && <span className="rounded-full bg-pink px-2 py-0.5 text-[10px] font-bold text-on-tint">PRIORITY</span>}
                {!rule.monitor && <span className="rounded-full bg-surface-2 px-2 py-0.5 text-[10px] font-semibold text-muted">IGNORED</span>}
                {rule.autoReply !== "Default" && (
                  <span className="rounded-full bg-surface-2 px-2 py-0.5 text-[10px] font-semibold text-muted">AUTO-REPLY: {rule.autoReply.toUpperCase()}</span>
                )}
                <span className="text-muted">{open ? "▾" : "▸"}</span>
              </button>
              {open && (
                <div className="space-y-2.5 border-t border-line p-3 text-sm">
                  <label className="flex items-center justify-between gap-3">
                    <span>
                      <span className="block font-semibold">Monitor this chat</span>
                      <span className="block text-xs text-muted">Off = Daksha ignores it: no notifications, no auto-reply, no rules.</span>
                    </span>
                    <Toggle checked={rule.monitor} onChange={(v) => void change(c.chatId, { monitor: v })} />
                  </label>
                  <label className="flex items-center justify-between gap-3">
                    <span>
                      <span className="block font-semibold">Auto-reply</span>
                      <span className="block text-xs text-muted">Default follows your Auto Reply settings; Always / Never override them for this chat.</span>
                    </span>
                    <Dropdown
                      value={rule.autoReply}
                      onChange={(v) => void change(c.chatId, { autoReply: v as WhatsAppChatRule["autoReply"] })}
                      className="w-32"
                      options={[{ value: "Default", label: "Default" }, { value: "Always", label: "Always" }, { value: "Never", label: "Never" }]}
                    />
                  </label>
                  <label className="flex items-center justify-between gap-3">
                    <span>
                      <span className="block font-semibold">Priority</span>
                      <span className="block text-xs text-muted">High-priority chats always notify, even under filtered notifications.</span>
                    </span>
                    <Dropdown
                      value={rule.priority}
                      onChange={(v) => void change(c.chatId, { priority: v as WhatsAppChatRule["priority"] })}
                      className="w-32"
                      options={[{ value: "Normal", label: "Normal" }, { value: "High", label: "High" }]}
                    />
                  </label>
                  <label className="flex items-center justify-between gap-3">
                    <span>
                      <span className="block font-semibold">Exclude from AI</span>
                      <span className="block text-xs text-muted">Daksha never sends this chat to the AI or answers it with the AI.</span>
                    </span>
                    <Toggle checked={rule.aiExcluded} onChange={(v) => void change(c.chatId, { aiExcluded: v })} />
                  </label>
                  <div>
                    <span className="block font-semibold">Custom reply for this chat</span>
                    <span className="block text-xs text-muted">If set, this fixed message is sent here instead of an AI reply. Saves when you click away.</span>
                    <textarea
                      defaultValue={rule.customReply ?? ""}
                      onBlur={(e) => { if ((rule.customReply ?? "") !== e.target.value.trim()) void change(c.chatId, { customReply: e.target.value.trim() }); }}
                      rows={2}
                      className={`${inputClass} mt-1.5`}
                    />
                  </div>
                </div>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
