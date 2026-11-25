import { SpotifyWorkspace } from "@/components/SpotifyWorkspace";
import { PageHeader } from "@/components/ui";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function SpotifyPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="Spotify">What is playing, your recent plays, playlists and search from Spotify.</PageHeader>
      <SpotifyWorkspace />
    </main>
  );
}
