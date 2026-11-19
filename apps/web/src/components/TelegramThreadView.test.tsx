import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TelegramThreadView } from "./TelegramThreadView";
import { dayLabel, sameDay } from "./ChatExtras";
import { Linkified } from "./Linkified";
import { api, type TelegramChat, type TelegramMessage } from "@/lib/api";

vi.mock("@/lib/signalr", () => ({ getIntegrationsHub: () => ({ on: vi.fn(), off: vi.fn() }) }));
vi.mock("./ChatAvatar", () => ({ ChatAvatar: () => null }));

const chat: TelegramChat = { chatId: "42", name: "Nenita", isGroup: false, lastMessagePreview: "", lastMessageAt: null, unreadCount: 0 };
const msg = (over: Partial<TelegramMessage>): TelegramMessage => ({ messageId: "1", chatId: "42", fromMe: true, isAiGenerated: false, senderName: "Me", text: "hello", timestamp: new Date().toISOString(), status: 2, ...over });
const mount = (messages: TelegramMessage[]) => {
  vi.spyOn(api, "telegramMessages").mockResolvedValue(messages);
  vi.spyOn(api, "telegramPresence").mockResolvedValue({ state: "typing" });
  vi.spyOn(api, "markTelegramChatRead").mockResolvedValue(undefined);
  return render(<TelegramThreadView connectionId="c1" chat={chat} />);
};
beforeEach(() => { Element.prototype.scrollTo = () => {}; Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true }); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe("Telegram thread", () => {
  it("shows ticks, typing, a reply quote and reactions, and marks the chat read", async () => {
    mount([msg({ messageId: "a", fromMe: false, text: "Hi", status: null }), msg({ messageId: "b", text: "Hey", status: 4, replyToText: "Hi", reactions: [{ emoji: "👍", count: 2, mine: false }] })]);
    expect(await screen.findByText("Hey")).toBeTruthy();
    expect(screen.getByRole("img", { name: "Read" })).toBeTruthy();
    expect(await screen.findByText("typing…")).toBeTruthy();
    expect(screen.getByText("👍 2")).toBeTruthy();
    await waitFor(() => expect(api.markTelegramChatRead).toHaveBeenCalledWith("c1", "42"));
  });

  it("shows a tombstone for a deleted message", async () => {
    mount([msg({ fromMe: false, deleted: true, text: "This message was deleted" })]);
    expect(await screen.findByText(/This message was deleted/)).toBeTruthy();
  });

  it("deletes for everyone and replies with the quoted message id", async () => {
    const del = vi.spyOn(api, "deleteTelegramMessage").mockResolvedValue(undefined);
    const send = vi.spyOn(api, "sendTelegramMessage").mockResolvedValue(msg({ messageId: "9" }));
    mount([msg({ text: "oops" })]);
    await screen.findByText("oops");
    await userEvent.click(screen.getByRole("button", { name: "Message options" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete…" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete for everyone" }));
    await waitFor(() => expect(del).toHaveBeenCalledWith("c1", "42", "1", true));
    await userEvent.click(screen.getByRole("button", { name: "Message options" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Reply" }));
    await userEvent.type(screen.getByPlaceholderText("Write a message…"), "ok");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(send).toHaveBeenCalledWith("c1", "42", "ok", "1"));
  });

  it("sends an attached file with a caption and loads older messages", async () => {
    const sendFile = vi.spyOn(api, "sendTelegramFile").mockResolvedValue(undefined);
    const older = vi.spyOn(api, "telegramLoadOlder").mockResolvedValue({ fetched: 5 });
    mount([]);
    await screen.findByText("No messages yet.");
    const file = new File(["abc"], "pic.jpg", { type: "image/jpeg" });
    await userEvent.upload(screen.getByLabelText("Attach a file"), file);
    await userEvent.type(screen.getByPlaceholderText("Add a caption…"), "look");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendFile).toHaveBeenCalledWith("c1", "42", file, { caption: "look", replyToId: undefined }));
    await userEvent.click(screen.getByRole("button", { name: "Load older messages" }));
    await waitFor(() => expect(older).toHaveBeenCalledWith("c1", "42"));
  });

});

describe("chat helpers", () => {
  it("labels days and detects day changes", () => {
    const now = new Date("2026-11-19T12:00:00");
    expect(dayLabel("2026-11-19T08:00:00", now)).toBe("Today");
    expect(dayLabel("2026-11-18T08:00:00", now)).toBe("Yesterday");
    expect(sameDay("2026-11-19T01:00:00", "2026-11-19T23:00:00")).toBe(true);
    expect(sameDay("2026-11-18T23:00:00", "2026-11-19T01:00:00")).toBe(false);
  });
  it("turns web addresses into safe links and keeps trailing punctuation out of them", () => {
    render(<p><Linkified text="See https://example.com/a?b=1, or www.test.org. Thanks" /></p>);
    const links = screen.getAllByRole("link");
    expect(links.map((l) => l.getAttribute("href"))).toEqual(["https://example.com/a?b=1", "https://www.test.org"]);
    expect(links[0].getAttribute("rel")).toContain("noopener");
  });
});
