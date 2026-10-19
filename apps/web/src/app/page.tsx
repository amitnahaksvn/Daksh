import Link from "next/link";

const FEATURES = [
  ["Assistant", "Converse with Daksh and keep a history you control."],
  ["Tasks & reminders", "Capture work and get notified on time."],
  ["Email & calendar", "Connect your accounts explicitly, revoke any time."],
  ["Privacy first", "Memory is opt-in. Every sensitive action is audited."],
];

export default function Home() {
  return (
    <main className="mx-auto w-full max-w-4xl px-4 py-16">
      <h1 className="text-4xl font-bold tracking-tight">Daksh</h1>
      <p className="mt-4 max-w-2xl text-lg text-neutral-700 dark:text-neutral-300">
        A personal AI operator that helps you manage conversations, tasks, reminders, email and calendar — with you in control of what it can access.
      </p>
      <div className="mt-8 flex gap-3">
        <Link href="/login" className="rounded-md bg-indigo-700 px-5 py-2.5 font-medium text-white hover:bg-indigo-800">Sign in</Link>
        <Link href="/dashboard" className="rounded-md border border-neutral-400 px-5 py-2.5 font-medium">Open dashboard</Link>
      </div>
      <ul className="mt-14 grid gap-6 sm:grid-cols-2">
        {FEATURES.map(([t, d]) => (
          <li key={t} className="rounded-lg border p-5">
            <h2 className="font-semibold">{t}</h2>
            <p className="mt-1 text-sm text-neutral-600 dark:text-neutral-400">{d}</p>
          </li>
        ))}
      </ul>
    </main>
  );
}
