import { MicrosoftWorkspace } from "@/components/MicrosoftWorkspace";
import { PageHeader } from "@/components/ui";

export default function MicrosoftPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="Microsoft">Outlook mail, Calendar, OneDrive and Azure DevOps through one Microsoft sign-in.</PageHeader>
      <MicrosoftWorkspace />
    </main>
  );
}
