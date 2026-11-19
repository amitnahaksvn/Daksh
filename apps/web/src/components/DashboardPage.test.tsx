import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DashboardContent as DashboardPage } from "./DashboardPage";
import { api, type DashboardApp } from "@/lib/api";

vi.mock("./DashboardWidgets", () => ({
  DashboardHero: ({ canResetLayout, onResetLayout, layoutSaveFailed, onRetryLayoutSave }: { canResetLayout?: boolean; onResetLayout?: () => void; layoutSaveFailed?: boolean; onRetryLayoutSave?: () => void }) => (
    <div>
      {canResetLayout && <button onClick={onResetLayout}>Reset layout</button>}
      {layoutSaveFailed && <button onClick={onRetryLayoutSave}>Couldn&apos;t save layout — retry</button>}
    </div>
  ),
}));
vi.mock("./CommandCentre", () => ({ CommandCentre: () => null }));
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
const mount = (apps: DashboardApp[]) => { vi.spyOn(api, "dashboardApps").mockResolvedValue(apps); return render(<DashboardPage widgets={["apps"]} />); };

describe("Dashboard layout", () => {
  it("shows tiles in the saved order and at the saved width", async () => {
    mount([tile("g", "gmail", "Gmail", 1), tile("n", "notion", "Notion", 0, 5)]);
    await screen.findByText("Gmail dashboard");
    expect(names()).toEqual(["n", "g"]); // Notion was saved first
    expect(document.querySelector('[data-tile="n"]')!.className).toContain("lg:col-span-5"); // saved 5 of 12 columns — not just thirds
    expect(document.querySelector('[data-tile="g"]')!.className).toContain("lg:col-span-8"); // Gmail's default
  });

  it("has no toolbar, and every tile can be resized from the left, right, top and bottom edges", async () => {
    mount([tile("g", "gmail", "Gmail", 0, 0, 420), tile("n", "notion", "Notion", 1)]);
    await screen.findByText("Gmail dashboard");
    expect(screen.queryByRole("toolbar")).not.toBeInTheDocument();
    for (const edge of ["left", "right", "top", "bottom", "corner"]) expect(document.querySelector(`[data-tile="g"] [data-edge="${edge}"]`)).toBeInTheDocument();
    expect((document.querySelector('[data-tile="g"]') as HTMLElement).style.height).toBe("420px"); // the saved height
  });

  it("Choose apps lists connected apps, ticks the ones already on the dashboard, and saves the changes", async () => {
    vi.spyOn(api, "availableDashboardApps").mockResolvedValue([
      { connectionId: "c1", integrationKey: "notion", name: "Notion", icon: "", color: "#000", accountIdentifier: "me@work.com", dashboardId: "n" },
      { connectionId: "c9", integrationKey: "gmail", name: "Gmail", icon: "", color: "#d33", accountIdentifier: "me@example.com", dashboardId: null },
    ]);
    const add = vi.spyOn(api, "addToDashboard").mockResolvedValue({ id: "x" });
    const remove = vi.spyOn(api, "removeFromDashboard").mockResolvedValue(undefined);
    mount([tile("n", "notion", "Notion", 0)]);
    await userEvent.click(await screen.findByRole("button", { name: "Choose apps" }));
    const boxes = await screen.findAllByRole("checkbox");
    expect(boxes.map((b) => (b as HTMLInputElement).checked)).toEqual([true, false]);
    await userEvent.click(boxes[1]); await userEvent.click(boxes[0]);
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(add).toHaveBeenCalledWith("c9");
    expect(remove).toHaveBeenCalledWith("n");
  });
});
