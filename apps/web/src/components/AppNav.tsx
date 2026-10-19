"use client";
import Link from "next/link";
import { usePathname } from "next/navigation";

export const NAV_ITEMS = [
  { href: "/dashboard", label: "Dashboard" },
  { href: "/assistant", label: "Assistant" },
  { href: "/tasks", label: "Tasks" },
  { href: "/reminders", label: "Reminders" },
  { href: "/email", label: "Email" },
  { href: "/calendar", label: "Calendar" },
  { href: "/integrations", label: "Integrations" },
  { href: "/privacy", label: "Privacy & memory" },
  { href: "/activity", label: "Activity" },
];

export function AppNav() {
  const pathname = usePathname();
  return (
    <nav aria-label="Main" className="flex gap-1 overflow-x-auto md:flex-col md:overflow-visible">
      {NAV_ITEMS.map((i) => (
        <Link
          key={i.href}
          href={i.href}
          aria-current={pathname === i.href ? "page" : undefined}
          className={`whitespace-nowrap rounded-md px-3 py-2 text-sm hover:bg-neutral-200 dark:hover:bg-neutral-800 ${
            pathname === i.href ? "bg-neutral-200 font-semibold dark:bg-neutral-800" : ""
          }`}
        >
          {i.label}
        </Link>
      ))}
    </nav>
  );
}
