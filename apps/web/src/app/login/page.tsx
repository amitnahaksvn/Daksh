import Link from "next/link";
import { AppearanceControls } from "@/components/ThemeToggle";
import { Orb } from "@/components/Orb";
import { buttonClass, Notice, tileClass } from "@/components/ui";

export default function LoginPage() {
  return (
    <main className="mx-auto w-full max-w-md p-3 py-10 md:p-4 md:py-16">
      <div className="mb-3 flex items-center justify-between px-2">
        <Link href="/" className="flex items-center gap-2.5 text-xl font-extrabold tracking-tight"><Orb size={26} />Daksh</Link>
        <AppearanceControls className="justify-end" />
      </div>
      <section className={`p-6 ${tileClass}`}>
        <h1 className="mb-4 text-2xl font-extrabold">Sign in</h1>
        <Notice>
          Identity provider integration is not configured yet (requirement AUTH-001). The API accepts OIDC bearer
          tokens when <code>Auth__Authority</code> is set; for local development use <code>NEXT_PUBLIC_DEV_AUTH=true</code>.
        </Notice>
        <Link href="/dashboard" className={`${buttonClass} w-full`}>Continue to dashboard (development)</Link>
      </section>
    </main>
  );
}
