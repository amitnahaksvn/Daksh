import { SlackWorkspace } from "@/components/SlackWorkspace";
import { PageHeader } from "@/components/ui";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function SlackPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="Slack">Channels and direct messages from your Slack workspaces.</PageHeader>
      <SlackWorkspace />
    </main>
  );
}
