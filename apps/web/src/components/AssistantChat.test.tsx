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
});
