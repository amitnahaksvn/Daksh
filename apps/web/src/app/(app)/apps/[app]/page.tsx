import { notFound } from "next/navigation";
import { ProviderAppWorkspace } from "@/components/ProviderApp";
import { isProviderAppKey, PROVIDER_APP_KEYS } from "@/lib/api";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

// One page for every provider-engine app (YouTube, Instagram, Shopify ...): it draws itself from the app's spec. Apps with their own
// folder (github, slack, ...) take precedence over this segment; any other name is a 404 (see notFound below).
export function generateStaticParams() {
  return PROVIDER_APP_KEYS.map((app) => ({ app }));
}

export default async function ProviderAppPage({ params }: { params: Promise<{ app: string }> }) {
  const { app } = await params;
  if (!isProviderAppKey(app)) notFound();
  return (
    <main className="w-full p-3 md:p-4">
      <ProviderAppWorkspace appKey={app} />
    </main>
  );
}
