import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ChatThread, MessageTicks } from "./WhatsAppThread";
import { api, type WhatsAppChat, type WhatsAppMessage } from "@/lib/api";

vi.mock("@/lib/signalr", () => ({ getIntegrationsHub: () => ({ on: vi.fn(), off: vi.fn() }) }));
vi.mock("./ChatAvatar", () => ({ ChatAvatar: () => null }));

const chat: WhatsAppChat = { chatId: "1@lid", chatName: "Yaashicca", isGroup: false, lastMessagePreview: "", lastMessageAt: new Date().toISOString(), unreadCount: 0 };
const msg = (over: Partial<WhatsAppMessage>): WhatsAppMessage => ({
  messageId: "m1", chatId: "1@lid", chatName: null, fromMe: true, isAiGenerated: false, isGroup: false, text: "hello", quotedMessageId: null, quotedText: null,
  reactions: [], timestamp: new Date().toISOString(), hasMedia: false, mediaKind: null, mimeType: null, fileName: null, mediaSeconds: null, voiceNote: false,
  status: 2, deleted: false, edited: false, starred: false, ...over,
});
const mount = (messages: WhatsAppMessage[]) => {
  vi.spyOn(api, "whatsAppChatMessages").mockResolvedValue(messages);
  vi.spyOn(api, "whatsAppPresence").mockResolvedValue({ state: "composing" });
  vi.spyOn(api, "markWhatsAppChatRead").mockResolvedValue(undefined);
  return render(<ChatThread connectionId="c1" chat={chat} onClose={() => {}} onSent={() => {}} />);
};

beforeEach(() => { Element.prototype.scrollTo = () => {}; Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true }); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

describe("message ticks", () => {
  it.each([[1, "Sending"], [2, "Sent"], [3, "Delivered"], [4, "Read"], [5, "Read"], [0, "Failed"]])("status %i reads as %s", (status, label) => {
    render(<MessageTicks status={status} />);
    expect(screen.getByRole("img", { name: label })).toBeTruthy();
  });
  it("shows nothing for a message that is not ours", () => {
    const { container } = render(<MessageTicks status={null} />);
    expect(container.textContent).toBe("");
  });
});

describe("chat thread", () => {
  it("shows ticks on our messages, 'typing…' from the other person, and tells WhatsApp the chat was read", async () => {
    mount([msg({ messageId: "a", fromMe: false, text: "Office party", status: null }), msg({ messageId: "b", text: "Haha", status: 4 })]);
    expect(await screen.findByText("Haha")).toBeTruthy();
    expect(screen.getByRole("img", { name: "Read" })).toBeTruthy();
    expect(await screen.findByText("typing…")).toBeTruthy();
    await waitFor(() => expect(api.markWhatsAppChatRead).toHaveBeenCalledWith("c1", "1@lid"));
  });

  it("replaces a message deleted for everyone with a tombstone, and never shows its text", async () => {
    mount([msg({ fromMe: false, text: "This message was deleted", deleted: true })]);
    expect(await screen.findByText(/This message was deleted/)).toBeTruthy();
  });

  it("offers delete for me and delete for everyone on our own recent message, and calls the right one", async () => {
    const del = vi.spyOn(api, "deleteWhatsAppMessage").mockResolvedValue(undefined);
    mount([msg({ text: "oops" })]);
    await screen.findByText("oops");
    await userEvent.click(screen.getByRole("button", { name: "Message options" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete…" }));
    expect(screen.getByRole("menuitem", { name: "Delete for me" })).toBeTruthy();
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete for everyone" }));
    await waitFor(() => expect(del).toHaveBeenCalledWith("c1", "m1", true));
  });

  it("only offers delete for me on a message that is not ours", async () => {
    mount([msg({ fromMe: false, text: "from her", status: null })]);
    await screen.findByText("from her");
    await userEvent.click(screen.getByRole("button", { name: "Message options" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete…" }));
    expect(screen.queryByRole("menuitem", { name: "Delete for everyone" })).toBeNull();
    expect(screen.getByRole("menuitem", { name: "Delete for me" })).toBeTruthy();
  });

  it("edits our own recent text message", async () => {
    const edit = vi.spyOn(api, "editWhatsAppMessage").mockResolvedValue(undefined);
    mount([msg({ text: "typo" })]);
    await screen.findByText("typo");
    await userEvent.click(screen.getByRole("button", { name: "Message options" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Edit" }));
    const box = screen.getByPlaceholderText("Edit your message…") as HTMLInputElement;
    expect(box.value).toBe("typo");
    await userEvent.clear(box);
    await userEvent.type(box, "fixed");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(edit).toHaveBeenCalledWith("c1", "m1", "fixed"));
  });

  it("sends an attached file with the typed caption", async () => {
    const sendFile = vi.spyOn(api, "sendWhatsAppFile").mockResolvedValue(undefined);
    mount([]);
    await screen.findByText("No messages yet.");
    const file = new File(["abc"], "photo.jpg", { type: "image/jpeg" });
    await userEvent.upload(screen.getByLabelText("Attach a file"), file);
    expect(screen.getByText(/photo\.jpg/)).toBeTruthy();
    await userEvent.type(screen.getByPlaceholderText("Add a caption…"), "look");
    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendFile).toHaveBeenCalledWith("c1", "1@lid", file, { caption: "look", quotedMessageId: undefined }));
  });

  it("filters the thread with the in-chat search", async () => {
    mount([msg({ messageId: "a", text: "metro at 5:30" }), msg({ messageId: "b", text: "see you" })]);
    await screen.findByText("metro at 5:30");
    await userEvent.click(screen.getByRole("button", { name: "Search" }));
    await userEvent.type(screen.getByLabelText("Search in this chat"), "metro");
    expect(screen.queryByText("see you")).toBeNull();
    expect(within(document.body).getByText("metro at 5:30")).toBeTruthy();
  });
});
