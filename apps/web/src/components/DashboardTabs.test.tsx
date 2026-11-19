import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DashboardTabs, TAB_KEY } from "./DashboardTabs";
import { api, type DashboardTab } from "@/lib/api";

vi.mock("./AssistantChat", () => ({ AssistantChat: () => <textarea aria-label="Message" /> }));
vi.mock("./DashboardPage", () => ({ DashboardContent: ({ widgets }: { widgets: string[] }) => <div data-testid="content">{widgets.join("+") || "none"}</div> }));

function memoryStorage(): Storage {
  const data = new Map<string, string>();
  return { get length() { return data.size; }, clear: () => data.clear(), getItem: (k) => data.get(k) ?? null, key: (i) => [...data.keys()][i] ?? null, removeItem: (k) => void data.delete(k), setItem: (k, v) => void data.set(k, String(v)) };
}
beforeEach(() => { Object.defineProperty(window, "localStorage", { value: memoryStorage(), configurable: true }); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const tab = (id: string, name: string, widgets: DashboardTab["widgets"] = ["command", "apps"]): DashboardTab => ({ id, name, order: 0, widgets });
const mount = (tabs: DashboardTab[]) => { vi.spyOn(api, "dashboards").mockResolvedValue(tabs); return render(<DashboardTabs />); };

describe("Dashboard tabs", () => {
  it("opens on Ask, a chat box, with the dashboards after it", async () => {
    mount([tab("d1", "Dashboard")]);
    expect(screen.getByRole("tab", { name: /Ask/ }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByLabelText("Message")).toBeTruthy();
    expect(await screen.findByRole("tab", { name: "Dashboard" })).toBeTruthy();
    const names = screen.getAllByRole("tab").map((t) => t.textContent?.trim());
    expect(names[0]).toContain("Ask");
    expect(names[1]).toBe("Dashboard");
  });

  it("shows a dashboard's blocks when its tab is chosen, and remembers the tab", async () => {
    mount([tab("d1", "Dashboard"), tab("d2", "Work", ["apps"])]);
    await userEvent.click(await screen.findByRole("tab", { name: "Work" }));
    expect(screen.getByTestId("content").textContent).toBe("apps");
    expect(window.localStorage.getItem(TAB_KEY)).toBe("d2");
  });

  it("adds a new dashboard, selects it and lets you name it straight away", async () => {
    const create = vi.spyOn(api, "createDashboard").mockResolvedValue(tab("d2", "Dashboard 2"));
    const update = vi.spyOn(api, "updateDashboard").mockResolvedValue(tab("d2", "Sales"));
    mount([tab("d1", "Dashboard")]);
    await screen.findByRole("tab", { name: "Dashboard" });
    await userEvent.click(screen.getByRole("button", { name: "New dashboard" }));
    await waitFor(() => expect(create).toHaveBeenCalled());
    const box = await screen.findByLabelText("Dashboard name");
    await userEvent.clear(box);
    await userEvent.type(box, "Sales{Enter}");
    await waitFor(() => expect(update).toHaveBeenCalledWith("d2", { name: "Sales" }));
    expect(await screen.findByRole("tab", { name: "Sales" })).toBeTruthy();
  });

  it("renames a dashboard by double-clicking its tab, and Escape cancels", async () => {
    const update = vi.spyOn(api, "updateDashboard").mockResolvedValue(tab("d1", "Home"));
    mount([tab("d1", "Dashboard")]);
    const t = await screen.findByRole("tab", { name: "Dashboard" });
    await userEvent.dblClick(t);
    await userEvent.type(screen.getByLabelText("Dashboard name"), "{Escape}");
    expect(update).not.toHaveBeenCalled();
    await userEvent.dblClick(await screen.findByRole("tab", { name: "Dashboard" }));
    const box = screen.getByLabelText("Dashboard name");
    await userEvent.clear(box);
    await userEvent.type(box, "Home{Enter}");
    await waitFor(() => expect(update).toHaveBeenCalledWith("d1", { name: "Home" }));
  });

  it("changes which blocks a dashboard shows from its menu", async () => {
    const update = vi.spyOn(api, "updateDashboard").mockResolvedValue(tab("d1", "Dashboard", ["command"]));
    mount([tab("d1", "Dashboard")]);
    await userEvent.click(await screen.findByRole("tab", { name: "Dashboard" }));
    await userEvent.click(screen.getByRole("button", { name: "Options for Dashboard" }));
    await userEvent.click(screen.getByLabelText("App tiles"));
    await waitFor(() => expect(update).toHaveBeenCalledWith("d1", { widgets: ["command"] }));
    expect(screen.getByTestId("content").textContent).toBe("command");
  });

  it("deletes a dashboard after confirming, but never the last one", async () => {
    const del = vi.spyOn(api, "deleteDashboard").mockResolvedValue(undefined);
    vi.spyOn(window, "confirm").mockReturnValue(true);
    mount([tab("d1", "Dashboard"), tab("d2", "Work")]);
    await userEvent.click(await screen.findByRole("tab", { name: "Work" }));
    await userEvent.click(screen.getByRole("button", { name: "Options for Work" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete dashboard" }));
    await waitFor(() => expect(del).toHaveBeenCalledWith("d2"));
    await waitFor(() => expect(screen.queryByRole("tab", { name: "Work" })).toBeNull());
    await userEvent.click(screen.getByRole("tab", { name: "Dashboard" }));
    await userEvent.click(screen.getByRole("button", { name: "Options for Dashboard" }));
    expect((screen.getByRole("menuitem", { name: "Delete dashboard" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("falls back to Ask when the remembered tab no longer exists", async () => {
    window.localStorage.setItem(TAB_KEY, "gone");
    mount([tab("d1", "Dashboard")]);
    await screen.findByRole("tab", { name: "Dashboard" });
    expect(screen.getByRole("tab", { name: /Ask/ }).getAttribute("aria-selected")).toBe("true");
  });
});
