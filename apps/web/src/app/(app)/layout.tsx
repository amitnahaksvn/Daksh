import Link from "next/link";
import { AppNav } from "@/components/AppNav";
import { AppearanceControls } from "@/components/ThemeToggle";
import { Orb } from "@/components/Orb";
import { tileClass } from "@/components/ui";

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="mx-auto grid w-full max-w-6xl flex-1 grid-cols-[minmax(0,1fr)] content-start gap-3 p-3 md:grid-cols-[15rem_minmax(0,1fr)] md:gap-4 md:p-4">
      <aside className={`flex min-w-0 flex-col gap-4 p-4 md:sticky md:top-4 md:h-[calc(100vh-2rem)] md:self-start md:overflow-y-auto ${tileClass}`}>
        <div className="flex items-center justify-between gap-3">
          <Link href="/" className="flex items-center gap-2.5 text-xl font-extrabold tracking-tight"><Orb size={26} />Daksh</Link>
          <AppearanceControls className="justify-end md:hidden" />
        </div>
        <AppNav />
        <div className="mt-auto hidden md:block">
          <p className="mb-2 px-1 text-xs font-semibold uppercase tracking-wide text-muted">Appearance</p>
          <AppearanceControls className="flex-col !items-start" />
        </div>
      </aside>
      <main id="main" className="min-w-0">{children}</main>
    </div>
  );
}
