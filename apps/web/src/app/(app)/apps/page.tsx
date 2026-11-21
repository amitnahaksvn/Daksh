import AppsClient from "./AppsClient";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function AppsPage() {
  return <AppsClient />;
}
