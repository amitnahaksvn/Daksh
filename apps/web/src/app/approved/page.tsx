import type { Metadata } from "next";
import { LegalPage } from "@/components/LegalPage";

export const metadata: Metadata = { title: "Approval received — Daksha" };

/** Where a company's IT administrator lands after approving Daksha from the one-time link. Public: they may have no Daksha account. */
export default async function ApprovedPage({ searchParams }: { searchParams: Promise<{ status?: string }> }) {
  const { status } = await searchParams;
  const ok = status !== "denied";
  return (
    <LegalPage title={ok ? "Approval received" : "Approval was not completed"}>
      {ok
        ? <p>Thank you. Daksha is now approved for your organization. People at your company can go back to Daksha and sign in with Microsoft. You can close this tab.</p>
        : <p>The approval was not completed, so Daksha was not approved. If this was a mistake, open the link you were sent and click Accept again. You can close this tab.</p>}
    </LegalPage>
  );
}
