import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DashboardPage } from "./DashboardPage";
import { api, type DashboardApp } from "@/lib/api";

vi.mock("./DashboardWidgets", () => ({
  DashboardHero: ({ canResetLayout, onResetLayout, layoutSaveFailed, onRetryLayoutSave }: { canResetLayout?: boolean; onResetLayout?: () => void; layoutSaveFailed?: boolean; onRetryLayoutSave?: () => void }) => (
    <div>
      {canResetLayout && <button onClick={onResetLayout}>Reset layout</button>}
      {layoutSaveFailed && <button onClick={onRetryLayoutSave}>Couldn&apos;t save layout — retry</button>}
    </div>
  ),
}));
vi.mock("./GmailDashboardCard", () => ({
  GmailDashboardCard: () => <div>Gmail dashboard</div>, GmailCardPlaceholder: () => null, GMAIL_CARD_HINT_KEY: "test.hint",
}));
vi.mock("next/link", () => ({ default: ({ href, children }: { href: string; children: React.ReactNode }) => <a href={href}>{children}</a> }));

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const tile = (id: string, key: string, name: string, order: number, cols = 0, height = 0, col = 0) => ({
  id, connectionId: `c-${id}`, integrationKey: key, name, icon: "mail", color: "#000", status: "Connected", accountIdentifier: null, lastActiveAt: null,
  displayOrder: order, unreadCount: 0, lastMessagePreview: null, lastMessageAt: null, lastMessageIsGroup: false, cols, height, col,
}) as DashboardApp;

const names = () => screen.getAllByRole("listitem").filter((li) => li.hasAttribute("data-tile")).map((li) => li.getAttribute("data-tile"));
const mount = (apps: DashboardApp[]) => { vi.spyOn(api, "dashboardApps").mockResolvedValue(apps); return render(<DashboardPage />); };

describe("Dashboard layout", () => {
  it("shows tiles in the saved order and at the saved width", async () => {
    mount([tile("g", "gmail", "Gmail", 1), tile("n", "notion", "Notion", 0, 5)]);
    await screen.findByText("Gmail dashboard");
    expect(names()).toEqual(["n", "g"]); // Notion was saved first
    expect(document.querySelector('[data-tile="n"]')!.className).toContain("lg:col-span-5"); // saved 5 of 12 columns — not just thirds
    expect(document.querySelector('[data-tile="g"]')!.className).toContain("lg:col-span-8"); // Gmail's default
  });

  it("moving a tile saves the new order to the account", async () => {
    mount([tile("g", "gmail", "Gmail", 0), tile("n", "notion", "Notion", 1)]);
    const save = vi.spyOn(api, "saveDashboardLayout").mockResolvedValue({ updated: 2 });
    await screen.findByText("Gmail dashboard");
    await userEvent.click(within(screen.getByRole("toolbar", { name: "Arrange Gmail" })).getByRole("button", { name: "Move Gmail later" }));
    expect(names()).toEqual(["n", "g"]);
    await waitFor(() => expect(save).toHaveBeenCalledTimes(1), { timeout: 2000 });
    expect(save.mock.calls[0][0]).toEqual([{ id: "n", displayOrder: 0, cols: 0, height: 0, col: 0 }, { id: "g", displayOrder: 1, cols: 0, height: 0, col: 0 }]);
    expect(screen.queryByText("Layout saved")).not.toBeInTheDocument(); // no visible "saved" banner any more
    expect(screen.queryByText(/Drag a tile to move it/)).not.toBeInTheDocument(); // and no instruction line
  });

  it("resizing steps one column at a time (3–12), saves the width, and the first tile can't move earlier", async () => {
    mount([tile("g", "gmail", "Gmail", 0), tile("n", "notion", "Notion", 1)]);
    const save = vi.spyOn(api, "saveDashboardLayout").mockResolvedValue({ updated: 2 });
    await screen.findByText("Gmail dashboard");
    const bar = within(screen.getByRole("toolbar", { name: "Arrange Gmail" }));
    expect(bar.getByRole("button", { name: "Move Gmail earlier" })).toBeDisabled();
    await userEvent.click(bar.getByRole("button", { name: "Make Gmail wider" }));
    expect(document.querySelector('[data-tile="g"]')!.className).toContain("lg:col-span-9"); // 8 → 9: one column at a time
    await userEvent.click(bar.getByRole("button", { name: "Make Gmail narrower" }));
    await userEvent.click(bar.getByRole("button", { name: "Make Gmail narrower" }));
    expect(document.querySelector('[data-tile="g"]')!.className).toContain("lg:col-span-7");
    await waitFor(() => expect(save).toHaveBeenCalled(), { timeout: 2000 });
    expect(save.mock.calls.at(-1)![0][0]).toEqual({ id: "g", displayOrder: 0, cols: 7, height: 0, col: 0 });
  });

  it("applies a saved height, makes a tile shorter, and can fit it back to its content", async () => {
    mount([tile("g", "gmail", "Gmail", 0, 0, 420), tile("n", "notion", "Notion", 1)]);
    const save = vi.spyOn(api, "saveDashboardLayout").mockResolvedValue({ updated: 2 });
    await screen.findByText("Gmail dashboard");
    const gmail = () => document.querySelector('[data-tile="g"]') as HTMLElement;
    expect(gmail().style.height).toBe("420px"); // the saved height
    const bar = within(screen.getByRole("toolbar", { name: "Arrange Gmail" }));
    await userEvent.click(bar.getByRole("button", { name: "Make Gmail shorter" }));
    expect(gmail().style.height).toBe("300px"); // 420 - 120
    await waitFor(() => expect(save).toHaveBeenCalled(), { timeout: 2000 });
    expect(save.mock.calls[0][0][0]).toEqual({ id: "g", displayOrder: 0, cols: 0, height: 300, col: 0 });
    await userEvent.click(bar.getByRole("button", { name: "Fit Gmail to its content" }));
    expect(gmail().style.height).toBe("");
    expect(bar.getByRole("button", { name: "Fit Gmail to its content" })).toBeDisabled();
  });

  it("never lets a tile be made shorter than the minimum", async () => {
    mount([tile("g", "gmail", "Gmail", 0, 0, 220), tile("n", "notion", "Notion", 1)]);
    await screen.findByText("Gmail dashboard");
    await userEvent.click(screen.getByRole("button", { name: "Make Gmail shorter" }));
    expect((document.querySelector('[data-tile="g"]') as HTMLElement).style.height).toBe("200px");
  });

  it("tells the user when saving fails and can retry", async () => {
    mount([tile("g", "gmail", "Gmail", 0), tile("n", "notion", "Notion", 1)]);
    const save = vi.spyOn(api, "saveDashboardLayout").mockRejectedValueOnce(new Error("offline")).mockResolvedValue({ updated: 2 });
    await screen.findByText("Gmail dashboard");
    await userEvent.click(screen.getByRole("button", { name: "Move Notion earlier" }));
    const retry = await screen.findByRole("button", { name: /Couldn't save layout/ }, { timeout: 2000 });
    await userEvent.click(retry);
    await waitFor(() => expect(save).toHaveBeenCalledTimes(2), { timeout: 2000 });
  });

  it("offers Reset layout only when customised, and resets on the server", async () => {
    mount([tile("g", "gmail", "Gmail", 0, 12), tile("n", "notion", "Notion", 1)]);
    const reset = vi.spyOn(api, "resetDashboardLayout").mockResolvedValue(undefined);
    await userEvent.click(await screen.findByRole("button", { name: "Reset layout" }));
    expect(reset).toHaveBeenCalledTimes(1);
    cleanup();
    mount([tile("g", "gmail", "Gmail", 0), tile("n", "notion", "Notion", 1)]);
    await screen.findByText("Gmail dashboard");
    expect(screen.queryByRole("button", { name: "Reset layout" })).not.toBeInTheDocument();
  });

  it("stops at the 3-column minimum and the 12-column maximum", async () => {
    mount([tile("g", "gmail", "Gmail", 0, 3), tile("n", "notion", "Notion", 1, 12)]);
    await screen.findByText("Gmail dashboard");
    expect(screen.getByRole("button", { name: "Make Gmail narrower" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Make Notion wider" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Make Gmail wider" })).toBeEnabled();
  });

  it("shifts a tile one column at a time with the toolbar and saves the start column", async () => {
    mount([tile("g", "gmail", "Gmail", 0, 6, 0, 3), tile("n", "notion", "Notion", 1)]);
    const save = vi.spyOn(api, "saveDashboardLayout").mockResolvedValue({ updated: 2 });
    await screen.findByText("Gmail dashboard");
    expect(document.querySelector('[data-tile="g"]')!.className).toContain("lg:col-start-3"); // the saved start column
    await userEvent.click(screen.getByRole("button", { name: "Shift Gmail one column right" }));
    // jsdom has no layout, so the measured start is column 1 → shifting right asks for column 2
    await waitFor(() => expect(save).toHaveBeenCalled(), { timeout: 2000 });
    expect(save.mock.calls.at(-1)![0][0]).toMatchObject({ id: "g", col: 2 });
  });
});
