"use client";

type Ad = { id: string; badge: string; title: string; body: string; cta: string; from: string; to: string };

// Placeholder ads (three tiles). They are clearly labelled and link nowhere; replace with a real ad source later.
const MOCK_ADS: Ad[] = [
  { id: "pro", badge: "Sponsored", title: "Daksha Pro", body: "Unlimited AI replies, priority sync and more connected apps for your whole team.", cta: "Learn more", from: "#f97316", to: "#9333ea" },
  { id: "cloud", badge: "Sponsored", title: "1 TB cloud storage", body: "Keep every file, photo and backup in one safe place. First 3 months free.", cta: "Try free", from: "#0ea5e9", to: "#14b8a6" },
  { id: "travel", badge: "Sponsored", title: "Weekend getaways", body: "Handpicked stays and flights from \u20B93,999. Book now, pay later.", cta: "See deals", from: "#e11d48", to: "#f59e0b" },
];

/** The right-hand column of the Ask page: three mock ads that share the height of the chat beside them. Hidden on small screens. */
export function AdSlots() {
  return (
    <aside aria-label="Sponsored" className="hidden h-[calc(100dvh-7.25rem)] min-h-0 flex-col gap-4 lg:flex">
      {MOCK_ADS.map((ad) => (
        <article key={ad.id} className="tile relative flex min-h-0 flex-1 flex-col justify-between overflow-hidden rounded-[1.25rem] border border-line p-4 text-white shadow-[var(--shadow)]"
          style={{ background: `linear-gradient(135deg, ${ad.from}, ${ad.to})` }}>
          <span className="ai-mono w-fit rounded-full bg-black/30 px-2.5 py-0.5 text-[11px] font-semibold uppercase tracking-wide">{ad.badge}</span>
          <div>
            <h2 className="text-xl font-extrabold leading-tight">{ad.title}</h2>
            <p className="mt-2 text-sm text-white/90">{ad.body}</p>
          </div>
          <button type="button" tabIndex={-1} aria-disabled="true" className="w-fit cursor-default rounded-full bg-white px-4 py-2 text-sm font-semibold text-black">{ad.cta}</button>
        </article>
      ))}
    </aside>
  );
}
