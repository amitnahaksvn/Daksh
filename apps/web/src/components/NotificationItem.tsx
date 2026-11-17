import Link from "next/link";
import type { AppNotification } from "@/lib/notifications";

export function timeAgo(iso: string): string {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return "now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

/** One notification row, shared by the bell dropdown and the full notifications page. */
export function NotificationItem({ n, onClick }: { n: AppNotification; onClick?: () => void }) {
  return (
    <Link
      href={`/apps/${n.integrationKey}`}
      onClick={onClick}
      className="flex items-start gap-3 rounded-xl px-3 py-2 text-sm transition-colors hover:bg-surface-2"
    >
      <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-xl bg-lime text-on-tint">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" className="h-4 w-4">
          <path d="M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v8a2.5 2.5 0 0 1-2.5 2.5H10l-4.5 4v-4A2.5 2.5 0 0 1 4 13.5v-8Z" />
        </svg>
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-1.5">
          <p className="font-semibold">{n.title}</p>
          {n.isGroup && <span className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold text-muted">GROUP</span>}
          {n.priority && <span className="rounded-full bg-pink px-1.5 py-0.5 text-[9px] font-bold text-on-tint">PRIORITY</span>}
          {n.tags?.filter((t) => t !== "link").slice(0, 3).map((t) => (
            <span key={t} className="ai-mono rounded-full bg-surface-2 px-1.5 py-0.5 text-[9px] font-semibold uppercase text-muted">{t.replace("-", " ")}</span>
          ))}
        </div>
        {n.body && <p className="mt-0.5 truncate text-muted">{n.body}</p>}
        <p className="mt-0.5 text-xs text-muted">{timeAgo(n.createdAt)}</p>
      </div>
    </Link>
  );
}
