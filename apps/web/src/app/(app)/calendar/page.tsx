import Link from "next/link";
import { Notice, PageHeader } from "@/components/ui";

export default function CalendarPage() {
  return (
    <>
      <PageHeader title="Calendar" />
      <Notice>Calendar access requires a calendar provider to be connected and verified. Currently only a mock adapter exists, so no real events are read or created.</Notice>
      <Link href="/integrations" className="text-indigo-700 underline">Manage integrations</Link>
    </>
  );
}
