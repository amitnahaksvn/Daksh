"use client";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { clearSession, SESSION_CHANGED_EVENT, SESSION_STORAGE_KEY } from "@/lib/session";
import type { AccountProfile } from "@/lib/api";

function subscribe(cb: () => void) {
  window.addEventListener("storage", cb);
  window.addEventListener(SESSION_CHANGED_EVENT, cb);
  return () => {
    window.removeEventListener("storage", cb);
    window.removeEventListener(SESSION_CHANGED_EVENT, cb);
  };
}
function readRaw() {
  try {
    return localStorage.getItem(SESSION_STORAGE_KEY);
  } catch {
    return null;
  }
}
function parse(raw: string | null): AccountProfile | null {
  try {
    return raw ? (JSON.parse(raw) as AccountProfile) : null;
  } catch {
    return null;
  }
}

/** The signed-in user's avatar + name, that opens a menu with Sign out. Sits next to the notification bell. */
export function UserBadge() {
  const router = useRouter();
  // Read the persisted session as a raw string (stable snapshot); null on the server so hydration matches.
  const raw = useSyncExternalStore(subscribe, readRaw, () => null);
  const user = parse(raw);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onClickAway = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", onClickAway);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onClickAway);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  if (!user) return null;

  const name = `${user.firstName} ${user.lastName}`.trim() || user.email;
  const initials = ((user.firstName?.[0] ?? "") + (user.lastName?.[0] ?? "") || user.email[0]).toUpperCase();

  function signOut() {
    clearSession();
    router.replace("/login");
  }

  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className="flex items-center gap-2.5 rounded-full py-0.5 pl-0.5 pr-2 transition-colors hover:bg-surface-2"
      >
        <span
          aria-hidden
          className="grid size-9 shrink-0 place-items-center rounded-full bg-accent text-sm font-bold text-accent-contrast"
        >
          {initials}
        </span>
        <span className="hidden min-w-0 text-left leading-tight sm:block">
          <span className="block max-w-40 truncate text-sm font-semibold">{name}</span>
          <span className="block max-w-40 truncate text-xs text-muted">{user.email}</span>
        </span>
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
          aria-hidden
          className={`size-4 shrink-0 text-muted transition-transform ${open ? "rotate-180" : ""}`}
        >
          <path d="m6 9 6 6 6-6" />
        </svg>
      </button>
      {open && (
        <div role="menu" className="absolute right-0 top-12 z-20 w-48 rounded-[1.25rem] border border-line bg-surface p-2 shadow-[var(--shadow)]">
          <Link
            href="/profile"
            role="menuitem"
            onClick={() => setOpen(false)}
            className="block w-full rounded-xl px-3 py-2 text-left text-sm font-medium transition-colors hover:bg-surface-2"
          >
            Profile
          </Link>
          <button
            type="button"
            role="menuitem"
            onClick={signOut}
            className="w-full rounded-xl px-3 py-2 text-left text-sm font-medium transition-colors hover:bg-surface-2"
          >
            Sign out
          </button>
        </div>
      )}
    </div>
  );
}
