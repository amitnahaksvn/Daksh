import { MicrosoftWorkspace } from "@/components/MicrosoftWorkspace";
import { PageHeader } from "@/components/ui";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function MicrosoftPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="Microsoft">Outlook mail, Calendar, OneDrive and Azure DevOps through one Microsoft sign-in.</PageHeader>
      <MicrosoftWorkspace />
    </main>
  );
}
