import Link from "next/link";
import { AppNav } from "@/components/AppNav";

export default function AppLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="mx-auto grid w-full max-w-6xl flex-1 gap-6 px-4 py-6 md:grid-cols-[14rem_1fr]">
      <aside>
        <Link href="/" className="mb-4 block text-xl font-bold">Daksh</Link>
        <AppNav />
      </aside>
      <main id="main" className="min-w-0">{children}</main>
    </div>
  );
}
