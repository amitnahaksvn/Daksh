"use client";
import { useEffect, useMemo, useRef, useState } from "react";

function escapeHtml(text: string) {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

/** Plain-text mail: keep line breaks, make bare URLs clickable. */
function textToHtml(text: string) {
  return escapeHtml(text).replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1">$1</a>');
}

function hasRemoteImages(html: string) {
  return /<img\b[^>]*\bsrc\s*=\s*["']?https?:/i.test(html) || /url\(\s*["']?https?:/i.test(html);
}

/**
 * Renders an email exactly as sent (HTML), the way Gmail does, but safely:
 *  - sandboxed iframe with scripts disabled (no allow-scripts), links open in a new tab;
 *  - a CSP that blocks every remote resource; remote images only load when the user opts in
 *    (per message or via the "Show images" setting). Photos in mail are hidden by default.
 */
export function GmailMessageBody({ html, text, showImages }: { html: string | null; text: string; showImages: boolean }) {
  const [allowImages, setAllowImages] = useState(showImages);
  const [height, setHeight] = useState(240);
  const frame = useRef<HTMLIFrameElement>(null);
  const source = html && html.trim() ? html : null;
  const blockedImages = useMemo(() => Boolean(source && hasRemoteImages(source)), [source]);

  const srcDoc = useMemo(() => {
    const csp = `default-src 'none'; style-src 'unsafe-inline'; img-src data:${allowImages ? " https:" : ""}; font-src data:`;
    const body = source ?? `<div style="white-space:pre-wrap">${textToHtml(text)}</div>`;
    return `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${csp}"><base target="_blank"><meta name="viewport" content="width=device-width,initial-scale=1"><style>
      html,body{margin:0;padding:0;background:#fff;color:#202124}
      body{font:14px/1.55 Roboto,Arial,sans-serif;padding:4px 2px;overflow-wrap:anywhere;word-break:break-word}
      img{max-width:100%;height:auto}table{max-width:100%}a{color:#1a0dab}
      blockquote{margin:8px 0 8px 4px;padding-left:12px;border-left:2px solid #dadce0;color:#5f6368}
    </style></head><body>${body}</body></html>`;
  }, [source, text, allowImages]);

  useEffect(() => {
    const el = frame.current;
    if (!el) return;
    const measure = () => {
      const doc = el.contentDocument;
      if (doc?.documentElement) setHeight(Math.max(120, doc.documentElement.scrollHeight + 8));
    };
    el.addEventListener("load", measure);
    const timer = window.setTimeout(measure, 300);
    return () => { el.removeEventListener("load", measure); window.clearTimeout(timer); };
  }, [srcDoc]);

  return (
    <div>
      {blockedImages && !allowImages && (
        <p className="mb-3 flex flex-wrap items-center gap-2 rounded-lg bg-surface-2 px-3 py-2 text-xs text-muted">
          Images in this message are hidden.
          <button onClick={() => setAllowImages(true)} className="font-semibold text-accent underline">Display images below</button>
        </p>
      )}
      <iframe
        ref={frame}
        title="Email content"
        sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
        srcDoc={srcDoc}
        style={{ height }}
        className="w-full rounded-lg border-0 bg-white"
      />
    </div>
  );
}
