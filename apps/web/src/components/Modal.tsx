"use client";
import { useEffect, type ReactNode } from "react";
import { tileClass } from "@/components/ui";

/** A large centered overlay dialog. Closes on Escape or a click outside the panel. */
export function Modal({ open, onClose, children, wide = false }: { open: boolean; onClose: () => void; children: ReactNode; wide?: boolean }) {
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.removeEventListener("keydown", onKey);
      document.body.style.overflow = previousOverflow;
    };
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div
      role="dialog"
      aria-modal="true"
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
      onClick={onClose}
    >
      <div
        className={`max-h-[96vh] w-full overflow-y-auto ${wide ? "max-w-[96rem] p-4 sm:p-6" : "max-w-2xl p-6 sm:p-8"} ${tileClass}`}
        onClick={(e) => e.stopPropagation()}
      >
        <button
          onClick={onClose}
          aria-label="Close"
          className="float-right -mr-1 -mt-1 rounded-full p-2 text-muted transition-colors hover:bg-surface-2 hover:text-text"
        >
          <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4">
            <path d="M3 3l10 10M13 3 3 13" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
          </svg>
        </button>
        {children}
      </div>
    </div>
  );
}
