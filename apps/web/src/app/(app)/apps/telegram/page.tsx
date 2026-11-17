import { TelegramConnectPanel } from "@/components/TelegramConnectPanel";
import { PageHeader, tileClass } from "@/components/ui";

export default function TelegramConfigPage() {
  return (
    <main className="w-full p-3 md:p-4">
      <PageHeader title="Telegram" />
      <div className={`p-6 ${tileClass}`}>
        <TelegramConnectPanel />
      </div>
    </main>
  );
}
