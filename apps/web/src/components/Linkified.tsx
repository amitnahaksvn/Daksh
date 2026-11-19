import type { ReactNode } from "react";

const URL_RE = /(https?:\/\/[^\s<>"']+|www\.[^\s<>"']+)/gi;
const TRAILING = /[).,!?;:'"\]]+$/;

/** Message text with web addresses turned into links (opened safely in a new tab). */
export function Linkified({ text }: { text: string }) {
  const out: ReactNode[] = [];
  let last = 0;
  for (const match of text.matchAll(URL_RE)) {
    const raw = match[0];
    const url = raw.replace(TRAILING, "");
    const start = match.index ?? 0;
    if (start > last) out.push(text.slice(last, start));
    const href = /^https?:\/\//i.test(url) ? url : `https://${url}`;
    out.push(<a key={start} href={href} target="_blank" rel="noopener noreferrer nofollow" className="underline underline-offset-2 hover:opacity-80">{url}</a>);
    last = start + url.length;
  }
  if (last < text.length) out.push(text.slice(last));
  return <>{out}</>;
}
