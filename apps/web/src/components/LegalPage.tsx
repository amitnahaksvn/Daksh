import Link from "next/link";
import { DakshaBrand } from "@/components/DakshaBrand";
import { tileClass } from "@/components/ui";

export const LEGAL_UPDATED = "18 November 2026";
export const LEGAL_CONTACT = "dakshatheprajapati@gmail.com";

export function LegalPage({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <main className="mx-auto w-full max-w-3xl p-3 md:p-4">
      <div className="mb-3 flex items-center justify-between px-2 py-2">
        <Link href="/" aria-label="Daksha home"><DakshaBrand /></Link>
        <nav className="flex gap-4 text-sm text-muted">
          <Link href="/legal/privacy" className="hover:underline">Privacy</Link>
          <Link href="/legal/terms" className="hover:underline">Terms</Link>
        </nav>
      </div>
      <article className={`space-y-4 px-6 py-8 sm:px-10 ${tileClass} [&_h2]:mt-6 [&_h2]:text-lg [&_h2]:font-bold [&_ul]:list-disc [&_ul]:pl-6 [&_li]:mt-1 [&_p]:leading-relaxed`}>
        <h1 className="text-3xl font-extrabold tracking-tight">{title}</h1>
        <p className="text-sm text-muted">Last updated: {LEGAL_UPDATED}</p>
        {children}
      </article>
    </main>
  );
}
