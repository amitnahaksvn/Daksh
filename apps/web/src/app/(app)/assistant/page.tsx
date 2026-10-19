import { AssistantChat } from "@/components/AssistantChat";
import { PageHeader } from "@/components/ui";

export default function AssistantPage() {
  return (
    <>
      <PageHeader title="Assistant">Replies are simulated until an AI provider is configured and verified.</PageHeader>
      <AssistantChat />
    </>
  );
}
