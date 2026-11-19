import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { AssistantChat } from "./AssistantChat";
import { api, ApiError } from "@/lib/api";

describe("AssistantChat", () => {
  it("shows the reply and labels simulated responses", async () => {
    vi.spyOn(api, "sendMessage").mockResolvedValue({ conversationId: "c1", messageId: "m1", reply: "[mock] hi", provider: "mock", isSimulated: true });
    render(<AssistantChat />);
    await userEvent.type(screen.getByLabelText("Message"), "hello");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(await screen.findByText(/\[mock\] hi/)).toBeInTheDocument();
    expect(screen.getByText("(simulated reply)")).toBeInTheDocument();
  });

  it("shows an accessible error when the API fails", async () => {
    vi.spyOn(api, "sendMessage").mockRejectedValue(new ApiError(500, "An unexpected error occurred"));
    render(<AssistantChat />);
    await userEvent.type(screen.getByLabelText("Message"), "hello");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("An unexpected error occurred");
  });

  it("does not send blank messages", async () => {
    const spy = vi.spyOn(api, "sendMessage");
    spy.mockClear();
    render(<AssistantChat />);
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(spy).not.toHaveBeenCalled();
  });

  it("shows that Daksh is thinking while the reply is on its way", async () => {
    let finish: (v: Awaited<ReturnType<typeof api.sendMessage>>) => void = () => {};
    vi.spyOn(api, "sendMessage").mockReturnValue(new Promise((resolve) => (finish = resolve)));
    render(<AssistantChat />);
    await userEvent.type(screen.getByLabelText("Message"), "hello");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Daksh is thinking");
    finish({ conversationId: "c1", messageId: "m1", reply: "[mock] done", provider: "mock", isSimulated: true });
    expect(await screen.findByText(/\[mock\] done/)).toBeInTheDocument();
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("shows which connected apps the assistant checked", async () => {
    vi.spyOn(api, "sendMessage").mockResolvedValue({ conversationId: "c1", messageId: "m1", reply: "Rohan asked about tonight.", provider: "gemini", isSimulated: false, sources: ["WhatsApp", "Gmail"] });
    render(<AssistantChat />);
    await userEvent.type(screen.getByLabelText("Message"), "what did Rohan say");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(await screen.findByText("Checked: WhatsApp, Gmail")).toBeInTheDocument();
  });

  it("asks once for permission to use the AI provider, then sends the held question", async () => {
    const send = vi.spyOn(api, "sendMessage")
      .mockRejectedValueOnce(new ApiError(403, "needs consent", {}, "ai_consent_required"))
      .mockResolvedValueOnce({ conversationId: "c1", messageId: "m1", reply: "Here you go.", provider: "gemini", isSimulated: false });
    vi.spyOn(api, "privacy").mockResolvedValue({ memoryEnabled: false, allowProviderDataSharing: false, retentionDays: 90 });
    const update = vi.spyOn(api, "updatePrivacy").mockResolvedValue({ memoryEnabled: false, allowProviderDataSharing: true, retentionDays: 90 });
    render(<AssistantChat />);
    await userEvent.type(screen.getByLabelText("Message"), "catch me up");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(await screen.findByText("Allow Daksha to use its AI provider?")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Allow and continue" }));
    expect(await screen.findByText("Here you go.")).toBeInTheDocument();
    expect(update).toHaveBeenCalledWith({ memoryEnabled: false, allowProviderDataSharing: true, retentionDays: 90 });
    expect(send).toHaveBeenCalledTimes(2);
    expect(screen.queryByText("Allow Daksha to use its AI provider?")).toBeNull();
  });
});
