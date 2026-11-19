"use client";
import { useEffect, useState } from "react";
import { api } from "@/lib/api";

/** One fetch per picture per page load; `null` remembers "this chat has no picture". */
const cache = new Map<string, Promise<string | null>>();
function pictureUrl(channel: "whatsapp" | "telegram", connectionId: string, chatId: string): Promise<string | null> {
  const key = `${channel}:${connectionId}:${chatId}`;
  let hit = cache.get(key);
  if (!hit) {
    hit = (channel === "telegram" ? api.telegramPicture(connectionId, chatId) : api.whatsAppPicture(connectionId, chatId)).then((blob) => (blob ? URL.createObjectURL(blob) : null), () => null);
    cache.set(key, hit);
  }
  return hit;
}

const initial = (name: string) => (name.trim().match(/[\p{L}\p{N}]/u)?.[0] ?? "?").toUpperCase();

/** A round profile picture (or group picture); falls back to the first letter while loading or when there is none. */
export function ChatAvatar({ connectionId, chatId, name, size = 40, isGroup = false, channel = "whatsapp", className = "" }: { connectionId: string; chatId: string; name: string; size?: number; isGroup?: boolean; channel?: "whatsapp" | "telegram"; className?: string }) {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    void pictureUrl(channel, connectionId, chatId).then((u) => { if (live) setUrl(u); });
    return () => { live = false; };
  }, [channel, connectionId, chatId]);
  const box = { width: size, height: size, fontSize: Math.max(11, Math.round(size * 0.4)) };
  if (url) {
    // eslint-disable-next-line @next/next/no-img-element
    return <img src={url} alt="" style={{ width: size, height: size }} className={`shrink-0 rounded-full object-cover ${className}`} />;
  }
  return (
    <span aria-hidden style={box} className={`flex shrink-0 items-center justify-center rounded-full bg-lime font-bold text-on-tint ${className}`}>
      {isGroup ? <svg viewBox="0 0 24 24" width={size * 0.55} height={size * 0.55} fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"><circle cx="9" cy="8" r="3" /><circle cx="17" cy="9" r="2.4" /><path d="M3.5 19c.5-3.2 2.6-5 5.5-5s5 1.8 5.5 5M15 14.5c2.6-.3 4.7 1 5.5 4" /></svg> : initial(name)}
    </span>
  );
}
