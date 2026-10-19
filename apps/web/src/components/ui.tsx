import type { ReactNode } from "react";

export function PageHeader({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <header className="mb-6">
      <h1 className="text-2xl font-semibold">{title}</h1>
      {children && <p className="mt-1 text-sm text-neutral-600 dark:text-neutral-400">{children}</p>}
    </header>
  );
}

export function Loading({ label = "Loading" }: { label?: string }) {
  return (
    <p role="status" aria-live="polite" className="text-sm text-neutral-500">
      {label}…
    </p>
  );
}

export function ErrorState({ error, onRetry }: { error: Error; onRetry?: () => void }) {
  return (
    <div role="alert" className="rounded-lg border border-red-300 bg-red-50 p-4 text-red-900 dark:border-red-800 dark:bg-red-950 dark:text-red-100">
      <p className="font-medium">Something went wrong</p>
      <p className="mt-1 text-sm">{error.message}</p>
      {onRetry && (
        <button onClick={onRetry} className="mt-3 rounded-md border border-red-400 px-3 py-1 text-sm hover:bg-red-100 dark:hover:bg-red-900">
          Try again
        </button>
      )}
    </div>
  );
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <p className="rounded-lg border border-dashed p-6 text-center text-sm text-neutral-500">{children}</p>;
}

export function Notice({ children }: { children: ReactNode }) {
  return <p className="mb-4 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-700 dark:bg-amber-950 dark:text-amber-100">{children}</p>;
}

export const buttonClass =
  "rounded-md bg-indigo-700 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-800 disabled:opacity-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-700";
export const inputClass =
  "w-full rounded-md border border-neutral-400 bg-transparent px-3 py-2 text-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-indigo-700";
