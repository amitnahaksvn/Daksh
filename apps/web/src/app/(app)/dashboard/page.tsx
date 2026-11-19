import { DashboardTabs } from "@/components/DashboardTabs";

// The dashboard renders entirely client-side behind auth, so it can't be validated for instant navigation.
export const instant = false;

export default function Page() {
  return <DashboardTabs />;
}
