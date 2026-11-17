import { WhatsAppConnectPanel } from "@/components/WhatsAppConnectPanel";
import { PageHeader, tileClass } from "@/components/ui";

export default function WhatsAppConfigPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="WhatsApp" />
      <div className={`p-6 ${tileClass}`}>
        <WhatsAppConnectPanel />
      </div>
    </main>
  );
}
