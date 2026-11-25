import { DiscordWorkspace } from "@/components/DiscordWorkspace";
import { PageHeader } from "@/components/ui";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function DiscordPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="Discord">Servers and channels your Discord bot can see.</PageHeader>
      <DiscordWorkspace />
    </main>
  );
}
