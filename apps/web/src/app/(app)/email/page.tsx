import Link from "next/link";
import { Notice, PageHeader } from "@/components/ui";

export default function EmailPage() {
  return (
    <>
      <PageHeader title="Email" />
      <Notice>Email access requires an email provider to be connected and verified. Currently only a mock adapter exists, so no real mail is read or sent.</Notice>
      <Link href="/integrations" className="text-indigo-700 underline">Manage integrations</Link>
    </>
  );
}
