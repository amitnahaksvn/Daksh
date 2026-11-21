import { GmailWorkspace } from "@/components/GmailWorkspace";
import { PageHeader } from "@/components/ui";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function GmailConfigPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="Gmail">Connect securely through Google sign-in, then search, read, and send mail.</PageHeader>
      <GmailWorkspace />
    </main>
  );
}
