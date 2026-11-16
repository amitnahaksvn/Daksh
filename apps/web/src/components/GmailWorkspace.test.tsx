import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { GmailWorkspace } from "./GmailWorkspace";
import { api } from "@/lib/api";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("GmailWorkspace", () => {
  it("does not reuse the inbox page token after a search reaches its last page", async () => {
    vi.spyOn(api, "appDetail").mockResolvedValue({
      catalog: {
        integrationKey: "gmail",
        name: "Gmail",
        description: "Email",
        icon: "mail",
        color: "#000000",
        category: "Productivity",
        isEnabled: true,
      },
      connection: {
        connectionId: "connection-1",
        integrationKey: "gmail",
        status: "Connected",
        accountIdentifier: "person@example.com",
        connectedAt: null,
        lastActiveAt: null,
        lastError: null,
      },
    });
    const messages = vi.spyOn(api, "gmailMessages")
      .mockResolvedValueOnce({ messages: [], nextPageToken: "inbox-token" })
      .mockResolvedValueOnce({ messages: [], nextPageToken: null });

    render(<GmailWorkspace />);
    expect(await screen.findByRole("button", { name: "Load more" })).toBeInTheDocument();

    await userEvent.clear(screen.getByRole("textbox", { name: "Search Gmail" }));
    await userEvent.type(screen.getByRole("textbox", { name: "Search Gmail" }), "from:person@example.com");
    await userEvent.click(screen.getByRole("button", { name: "Search" }));

    await waitFor(() => expect(messages).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument());
  });
});
