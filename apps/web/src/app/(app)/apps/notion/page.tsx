import { NotionWorkspace } from "@/components/NotionWorkspace";
import { PageHeader } from "@/components/ui";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function NotionPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="Notion">Search, read and (with full access) write the Notion pages you share with Daksha.</PageHeader>
      <NotionWorkspace />
    </main>
  );
}
