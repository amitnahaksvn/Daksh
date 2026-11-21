import CalendarClient from "./CalendarClient";

// Behind <RequireAuth>, which renders nothing on the server, so Next.js cannot render this segment to check it for instant navigation.
export const instant = false;

export default function CalendarPage() {
  return <CalendarClient />;
}
