"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";

export const NAV_ITEMS = [
  { href: "/dashboard", label: "Dashboard" },
  { href: "/apps", label: "Apps" },
  { href: "/lists", label: "Lists" },
];

export function AppNav() {
  const pathname = usePathname();
  return (
    <nav aria-label="Main" className="flex shrink-0 gap-1 overflow-x-auto pb-0.5 md:flex-col md:overflow-visible md:pb-0">
      {NAV_ITEMS.map((i) => (
        <Link
          key={i.href}
          href={i.href}
          aria-current={pathname === i.href ? "page" : undefined}
          className={`whitespace-nowrap rounded-full px-4 py-2.5 text-sm font-medium transition-colors ${
            pathname === i.href ? "bg-accent text-accent-contrast" : "text-muted hover:bg-surface-2 hover:text-text"
          }`}
        >
          {i.label}
        </Link>
      ))}
    </nav>
  );
}
