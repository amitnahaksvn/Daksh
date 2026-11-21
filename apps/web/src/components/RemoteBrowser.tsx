"use client";
import { useEffect, useRef, useState } from "react";

/**
 * A live view of the browser Daksha opened on its server for you to sign in to a shopping site. Frames arrive over a WebSocket; your clicks, scrolls and
 * typing are sent back, so you enter your own password, codes and captchas yourself. It shows only that one sign-in window, and closes by itself once you are in.
 */
export function RemoteBrowser({ url, onDone }: { url: string; onDone: () => void }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const socket = useRef<WebSocket | null>(null);
  const size = useRef({ width: 1100, height: 720 });
  const [status, setStatus] = useState<"connecting" | "live" | "ended" | "failed">("connecting");
  const done = useRef(onDone);
  useEffect(() => { done.current = onDone; }, [onDone]);

  useEffect(() => {
    const ws = new WebSocket(url);
    socket.current = ws;
    ws.onmessage = (e) => {
      let m: { type: string; data?: string; width?: number; height?: number };
      try { m = JSON.parse(String(e.data)); } catch { return; }
      if (m.type === "ready" && m.width && m.height) {
        size.current = { width: m.width, height: m.height };
        if (canvas.current) { canvas.current.width = m.width; canvas.current.height = m.height; }
        setStatus("live");
      } else if (m.type === "frame" && m.data) {
        const img = new Image();
        img.onload = () => canvas.current?.getContext("2d")?.drawImage(img, 0, 0, size.current.width, size.current.height);
        img.src = `data:image/jpeg;base64,${m.data}`;
      } else if (m.type === "done") { setStatus("ended"); done.current(); }
    };
    ws.onclose = (e) => { setStatus((s) => (s === "ended" ? s : e.code === 1000 ? "ended" : "failed")); if (e.code === 1000) done.current(); };
    ws.onerror = () => setStatus("failed");
    return () => { ws.onmessage = null; ws.close(); };
  }, [url]);

  const send = (m: object) => { if (socket.current?.readyState === WebSocket.OPEN) socket.current.send(JSON.stringify(m)); };
  const point = (e: { clientX: number; clientY: number }) => {
    const r = canvas.current!.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * size.current.width, y: ((e.clientY - r.top) / r.height) * size.current.height };
  };
  const button = (b: number) => (b === 2 ? "right" : b === 1 ? "middle" : "left");

  // The wheel needs a non-passive listener so scrolling the remote page does not also scroll this one.
  useEffect(() => {
    const c = canvas.current;
    if (!c) return;
    const onWheel = (e: WheelEvent) => { e.preventDefault(); send({ type: "wheel", ...point(e), dx: e.deltaX, dy: e.deltaY }); };
    c.addEventListener("wheel", onWheel, { passive: false });
    return () => c.removeEventListener("wheel", onWheel);
  }, []);

  return (
    <div>
      <div className="relative overflow-hidden rounded-xl border border-line bg-black">
        <canvas
          ref={canvas} width={1100} height={720} tabIndex={0} aria-label="Sign-in browser"
          className="block h-auto w-full cursor-default outline-none focus:ring-2 focus:ring-accent"
          onPointerMove={(e) => send({ type: "mouse", kind: "move", ...point(e) })}
          onPointerDown={(e) => { e.currentTarget.focus(); send({ type: "mouse", kind: "down", button: button(e.button), ...point(e) }); }}
          onPointerUp={(e) => send({ type: "mouse", kind: "up", button: button(e.button), ...point(e) })}
          onContextMenu={(e) => e.preventDefault()}
          onKeyDown={(e) => { if (e.metaKey || e.ctrlKey) return; e.preventDefault(); send({ type: "key", kind: "down", key: e.key }); }}
          onKeyUp={(e) => { if (e.metaKey || e.ctrlKey) return; e.preventDefault(); send({ type: "key", kind: "up", key: e.key }); }}
          onPaste={(e) => { const t = e.clipboardData.getData("text"); if (t) { e.preventDefault(); send({ type: "text", text: t.slice(0, 200) }); } }}
        />
        {status !== "live" && (
          <div role="status" className="absolute inset-0 flex items-center justify-center bg-black/70 p-4 text-center text-sm text-white">
            {status === "connecting" ? "Opening the sign-in window…" : status === "ended" ? "Signed in. Finishing up…" : "The sign-in window could not be shown. Close this and try again."}
          </div>
        )}
      </div>
      <p className="mt-2 text-xs text-muted">This is a live view of a private browser. Click in it and type your own details, including any code or captcha. Daksha does not read what you type, and this closes by itself when you are in.</p>
    </div>
  );
}
