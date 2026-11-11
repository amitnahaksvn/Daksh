import Link from "next/link";
import { AppearanceControls } from "@/components/ThemeToggle";
import { Orb } from "@/components/Orb";
import { buttonClass, secondaryButtonClass, tileClass } from "@/components/ui";

const FEATURES: [string, string, string][] = [
  ["Assistant", "Converse with Daksh and keep a history you control.", "bg-lime text-on-tint"],
  ["Tasks & reminders", "Capture work and get notified on time.", "bg-pink text-on-tint"],
  ["Email & calendar", "Connect your accounts explicitly, revoke any time.", "bg-surface border border-line"],
  ["Privacy first", "Memory is opt-in. Every sensitive action is audited.", "bg-surface border border-line"],
];

export default function Home() {
  return (
    <main className="mx-auto w-full max-w-4xl p-3 md:p-4">
      <div className="mb-3 flex items-center justify-between px-2 py-2">
        <span className="flex items-center gap-2.5 text-xl font-extrabold tracking-tight"><Orb size={28} />Daksh</span>
        <AppearanceControls className="justify-end" />
      </div>
      <section className={`px-6 py-14 text-center sm:px-14 sm:py-20 ${tileClass}`}>
        <div className="mb-6 flex justify-center"><Orb size={88} /></div>
        <h1 className="ai-gradient-text text-4xl font-extrabold tracking-tight sm:text-6xl">Your personal AI operator.</h1>
        <p className="mx-auto mt-5 max-w-2xl text-lg text-muted">
          A personal AI operator that helps you manage conversations, tasks, reminders, email and calendar — with you in control of what it can access.
        </p>
        <div className="mt-8 flex flex-wrap justify-center gap-3">
          <Link href="/login" className={`${buttonClass} px-6 py-3 text-base`}>Sign in</Link>
          <Link href="/dashboard" className={`${secondaryButtonClass} px-6 py-3 text-base`}>Open dashboard</Link>
        </div>
      </section>
      <ul className="mt-3 grid gap-3 sm:grid-cols-2">
        {FEATURES.map(([t, d, tone]) => (
          <li key={t} className={`rounded-[1.25rem] p-6 ${tone}`}>
            <h2 className="text-lg font-bold">{t}</h2>
            <p className="mt-1.5 text-sm opacity-80">{d}</p>
          </li>
        ))}
      </ul>
    </main>
  );
}
