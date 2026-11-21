"use client";
import { useSyncExternalStore } from "react";
import { LegalPage } from "@/components/LegalPage";

const subscribe = () => () => undefined;
const readStatus = () => new URLSearchParams(window.location.search).get("status") ?? "ok";

/** Where a company's IT administrator lands after approving Daksha from the one-time link. Public: they may have no Daksha account. Reads the URL in the browser so it also works in the static export. */
export default function ApprovedPage() {
  const status = useSyncExternalStore(subscribe, readStatus, () => "ok");
  const ok = status !== "denied";
  return (
    <LegalPage title={ok ? "Approval received" : "Approval was not completed"}>
      {ok
        ? <p>Thank you. Daksha is now approved for your organization. People at your company can go back to Daksha and sign in with Microsoft. You can close this tab.</p>
        : <p>The approval was not completed, so Daksha was not approved. If this was a mistake, open the link you were sent and click Accept again. You can close this tab.</p>}
    </LegalPage>
  );
}
