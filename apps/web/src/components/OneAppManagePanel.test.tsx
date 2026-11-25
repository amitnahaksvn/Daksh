import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OneAppManagePanel } from "./OneAppManagePanel";
import { ONE_APPS } from "./oneAppConfigs";
import { api, type OneAppKey } from "@/lib/api";

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const connected = (key: OneAppKey) => ({ configured: true, connected: true, connectionId: `c-${key}`, accountId: "me", accountName: "me", write: false, scopes: "", extra: {}, error: null });

describe("OneAppManagePanel", () => {
  it("offers the General tab plus a tab for each part of the app", async () => {
    vi.spyOn(api, "oneAppStatus").mockResolvedValue(connected("github"));
    vi.spyOn(api, "availableDashboardApps").mockResolvedValue([]);
    vi.spyOn(api, "oneAppSettings").mockResolvedValue({ itemCount: 5, dashboardSource: "review", includeIssues: true, includeNotifications: true, hideDrafts: false, showOnDashboard: false });
    render(<OneAppManagePanel config={ONE_APPS.github} />);
    for (const name of ["General", "Work", "Notifications"]) expect(await screen.findByRole("tab", { name })).toBeInTheDocument();
    expect(screen.getByText("Allow Daksh to comment")).toBeInTheDocument();
    expect(screen.getByText("Include private repositories")).toBeInTheDocument();
  });

  it("shows the sign-in card when not connected", async () => {
    vi.spyOn(api, "oneAppStatus").mockResolvedValue({ ...connected("spotify"), connected: false, connectionId: null, accountName: null });
    vi.spyOn(api, "availableDashboardApps").mockResolvedValue([]);
    render(<OneAppManagePanel config={ONE_APPS.spotify} />);
    expect(await screen.findByRole("button", { name: "Continue with Spotify" })).toBeInTheDocument();
    expect(screen.queryByRole("tab")).not.toBeInTheDocument();
  });

  it("asks Discord users for a bot token instead of a sign-in", async () => {
    vi.spyOn(api, "oneAppStatus").mockResolvedValue({ ...connected("discord"), connected: false, connectionId: null, accountName: null });
    vi.spyOn(api, "availableDashboardApps").mockResolvedValue([]);
    render(<OneAppManagePanel config={ONE_APPS.discord} />);
    expect(await screen.findByLabelText("Discord bot token")).toBeInTheDocument();
  });
});
