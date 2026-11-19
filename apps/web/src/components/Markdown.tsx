import type { ReactNode } from "react";
import { Linkified } from "./Linkified";

/** **bold**, *italic*, `code` inside one line of text. Only closed pairs count, so a half-typed reply never flickers. */
function inline(text: string, keyBase: string): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /(\*\*[^*\n]+\*\*|`[^`\n]+`|(?<![\w*])\*[^*\s][^*\n]*?\*(?![\w*]))/g;
  let last = 0; let n = 0;
  for (const m of text.matchAll(re)) {
    const at = m.index ?? 0;
    if (at > last) out.push(<Linkified key={`${keyBase}t${n++}`} text={text.slice(last, at)} />);
    const tok = m[0];
    if (tok.startsWith("**")) out.push(<strong key={`${keyBase}b${n++}`} className="font-semibold">{inline(tok.slice(2, -2), `${keyBase}b${n}`)}</strong>);
    else if (tok.startsWith("`")) out.push(<code key={`${keyBase}c${n++}`} className="ai-mono rounded bg-surface px-1 py-0.5 text-[0.85em]">{tok.slice(1, -1)}</code>);
    else out.push(<em key={`${keyBase}i${n++}`}>{tok.slice(1, -1)}</em>);
    last = at + tok.length;
  }
  if (last < text.length) out.push(<Linkified key={`${keyBase}t${n++}`} text={text.slice(last)} />);
  return out;
}

type Block = { kind: "p" | "h"; text: string } | { kind: "ul"; items: string[] } | { kind: "ol"; items: string[] };

function parse(text: string): Block[] {
  const blocks: Block[] = [];
  for (const raw of text.replace(/\r/g, "").split("\n")) {
    const line = raw.trimEnd();
    if (!line.trim()) { blocks.push({ kind: "p", text: "" }); continue; }
    const bullet = line.match(/^\s*[*\-•]\s+(.*)$/);
    const numbered = line.match(/^\s*\d+[.)]\s+(.*)$/);
    const heading = line.match(/^\s*#{1,4}\s+(.*)$/);
    const prev = blocks[blocks.length - 1];
    if (bullet) { if (prev?.kind === "ul") prev.items.push(bullet[1]); else blocks.push({ kind: "ul", items: [bullet[1]] }); }
    else if (numbered) { if (prev?.kind === "ol") prev.items.push(numbered[1]); else blocks.push({ kind: "ol", items: [numbered[1]] }); }
    else if (heading) blocks.push({ kind: "h", text: heading[1] });
    else blocks.push({ kind: "p", text: line.trim() });
  }
  return blocks;
}

/** A reply's text laid out properly: paragraphs, bullet and numbered lists, headings, bold, italics, code and links. */
export function Markdown({ text }: { text: string }) {
  const blocks = parse(text).filter((b) => !(b.kind === "p" && b.text === ""));
  return (
    <div className="flex flex-col gap-2 leading-relaxed">
      {blocks.map((b, i) => {
        const key = `k${i}`;
        if (b.kind === "ul") return <ul key={key} className="list-disc space-y-1 pl-5">{b.items.map((t, j) => <li key={j}>{inline(t, `${key}.${j}`)}</li>)}</ul>;
        if (b.kind === "ol") return <ol key={key} className="list-decimal space-y-1 pl-5">{b.items.map((t, j) => <li key={j}>{inline(t, `${key}.${j}`)}</li>)}</ol>;
        if (b.kind === "h") return <p key={key} className="font-bold">{inline(b.text, key)}</p>;
        return <p key={key}>{inline(b.text, key)}</p>;
      })}
    </div>
  );
}
