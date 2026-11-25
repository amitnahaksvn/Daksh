import { GitHubWorkspace } from "@/components/GitHubWorkspace";
import { PageHeader } from "@/components/ui";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function GitHubPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="GitHub">Pull requests, issues, notifications and repositories from your GitHub account.</PageHeader>
      <GitHubWorkspace />
    </main>
  );
}
