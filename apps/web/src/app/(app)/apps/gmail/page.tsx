import { GmailWorkspace } from "@/components/GmailWorkspace";
import { PageHeader } from "@/components/ui";

export default function GmailConfigPage() {
  return (
    <main className="mx-auto w-full max-w-6xl p-3 md:p-4">
      <PageHeader title="Gmail">Connect securely through Google sign-in, then search, read, and send mail.</PageHeader>
      <GmailWorkspace />
    </main>
  );
}
