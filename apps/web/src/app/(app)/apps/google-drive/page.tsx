import { GoogleWorkspaceWorkspace } from "@/components/GoogleWorkspaceWorkspace";
import { PageHeader } from "@/components/ui";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function GoogleDrivePage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="Google Drive">Drive, Docs, Sheets and Slides through your Google sign-in.</PageHeader>
      <GoogleWorkspaceWorkspace />
    </main>
  );
}
