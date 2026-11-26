import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OneAppManagePanel } from "./OneAppManagePanel";
import { configFromSpec, ProviderAppWorkspace, ProviderConnectCard } from "./ProviderApp";
import { api, PROVIDER_APP_KEYS, type OneAppStatus, type ProviderSpec } from "@/lib/api";

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const status = (over: Partial<OneAppStatus> = {}): OneAppStatus => ({ configured: true, connected: true, connectionId: "c-1", accountId: "me", accountName: "me", write: false, scopes: "", extra: {}, error: null, ...over });

const spec = (over: Partial<ProviderSpec> = {}): ProviderSpec => ({
  key: "youtube", name: "YouTube", icon: "SiYoutube", color: "#FF0000", account: "Subscriptions and playlists",
  connectTitle: "Connect your YouTube account", connectBlurb: "Sign in with Google.", connectLabel: "Continue with Google", writeLabel: "Allow Daksh to manage playlists", writeHint: "Full access lets Daksha add videos.",
  auth: "oauth", writeNeedsSignIn: true, hasWrite: true, setupDoc: "Amit.md#youtube", connectFields: [],
  views: [
    { id: "playlists", label: "Playlists", takesArg: false, hidden: false, dashboard: true, empty: null, inputs: [] },
    { id: "playlist-items", label: "Playlist", takesArg: true, hidden: true, dashboard: false, empty: null, inputs: [] },
    { id: "search", label: "Search", takesArg: false, hidden: false, dashboard: false, empty: null, inputs: [{ key: "q", label: "Search YouTube", kind: "text", placeholder: null, required: true, options: null, optionsView: null, default: null, hint: null }] },
  ],
  actions: [{ id: "create-playlist", label: "Create a playlist", hint: null, inputs: [{ key: "title", label: "Title", kind: "text", placeholder: null, required: true, options: null, optionsView: null, default: null, hint: null }] }],
  settings: [
    { key: "dashboardView", label: "What the dashboard tile shows", type: "enum", hint: null, placeholder: null, options: ["playlists"], default: "playlists" },
    { key: "itemCount", label: "Items to show", type: "count", hint: null, placeholder: null, options: null, default: "5" },
    { key: "pageName", label: "Which Page", type: "text", hint: null, placeholder: "My business", options: null, default: "" },
  ],
  ...over,
});

describe("provider apps", () => {
  it("lists every app once", () => {
    expect(new Set(PROVIDER_APP_KEYS).size).toBe(PROVIDER_APP_KEYS.length);
    for (const key of ["youtube", "instagram-social", "shopify", "gitlab", "stripe"]) expect(PROVIDER_APP_KEYS).toContain(key);
  });

  it("builds the Manage popup tabs from the spec", () => {
    const config = configFromSpec(spec());
    expect(config.tabs.map((t) => t.label)).toEqual(["Dashboard", "Preferences"]);
    expect(config.tabs[0].sections[0].rows.map((r) => r.key)).toEqual(["dashboardView", "itemCount"]);
    expect(config.tabs[0].sections[0].rows[0].options).toEqual([{ value: "playlists", label: "Playlists" }]);
    expect(config.pageHref).toBe("/apps/youtube");
    expect(config.writeNeedsSignIn).toBe(true);
    expect(config.hideWrite).toBe(false);
    expect(configFromSpec(spec({ hasWrite: false })).hideWrite).toBe(true);
  });

  it("shows General plus a tab per part in the Manage popup, like Gmail and WhatsApp", async () => {
    vi.spyOn(api, "oneAppStatus").mockResolvedValue(status());
    vi.spyOn(api, "availableDashboardApps").mockResolvedValue([]);
    vi.spyOn(api, "oneAppSettings").mockResolvedValue({ dashboardView: "playlists", itemCount: 5, pageName: "", showOnDashboard: false });
    render(<OneAppManagePanel config={configFromSpec(spec())} />);
    for (const name of ["General", "Dashboard", "Preferences"]) expect(await screen.findByRole("tab", { name })).toBeInTheDocument();
    expect(screen.getByText("Allow Daksh to manage playlists")).toBeInTheDocument();
    expect(screen.getByText("Show YouTube on dashboard")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Disconnect" })).toBeInTheDocument();
  });

  it("hides the full-access switch for read-only apps", async () => {
    vi.spyOn(api, "oneAppStatus").mockResolvedValue(status());
    vi.spyOn(api, "availableDashboardApps").mockResolvedValue([]);
    vi.spyOn(api, "oneAppSettings").mockResolvedValue({ dashboardView: "playlists", itemCount: 5, pageName: "", showOnDashboard: false });
    render(<OneAppManagePanel config={configFromSpec(spec({ hasWrite: false }))} />);
    expect(await screen.findByText(/is read-only/)).toBeInTheDocument();
    expect(screen.queryByText("Allow Daksh to manage playlists")).not.toBeInTheDocument();
  });

  it("asks for the details a credential app needs and connects with them", async () => {
    const connect = vi.spyOn(api, "connectProviderApp").mockResolvedValue(undefined);
    vi.spyOn(api, "oneAppStatus").mockResolvedValue(status({ connected: false }));
    const onConnected = vi.fn();
    const ha = spec({
      key: "home-assistant", name: "Home Assistant", auth: "credentials", connectLabel: "Connect Home Assistant", writeNeedsSignIn: false,
      connectFields: [
        { key: "url", label: "Home Assistant address", kind: "url", placeholder: "http://homeassistant.local:8123", required: true, options: null, optionsView: null, default: null, hint: null },
        { key: "token", label: "Long-lived access token", kind: "secret", placeholder: null, required: true, options: null, optionsView: null, default: null, hint: null },
      ],
    });
    render(<ProviderConnectCard spec={ha} onConnected={onConnected} />);
    const button = screen.getByRole("button", { name: "Connect Home Assistant" });
    expect(button).toBeDisabled(); // nothing entered yet
    expect(screen.getByLabelText("Long-lived access token")).toHaveAttribute("type", "password");
    fireEvent.change(screen.getByLabelText("Home Assistant address"), { target: { value: "http://ha.local:8123" } });
    fireEvent.change(screen.getByLabelText("Long-lived access token"), { target: { value: "TOKEN" } });
    fireEvent.click(button);
    await waitFor(() => expect(onConnected).toHaveBeenCalled());
    expect(connect).toHaveBeenCalledWith("home-assistant", { url: "http://ha.local:8123", token: "TOKEN" });
  });

  it("explains when an app's keys are not on the server yet", async () => {
    vi.spyOn(api, "oneAppStatus").mockResolvedValue(status({ connected: false, configured: false }));
    render(<ProviderConnectCard spec={spec()} onConnected={() => {}} />);
    expect(await screen.findByText(/not set up on the server yet/)).toBeInTheDocument();
    expect(screen.getByText(/Amit\.md#youtube/)).toBeInTheDocument();
  });

  it("lists each part as a tab, loads its rows and drills into a row", async () => {
    vi.spyOn(api, "providerSpec").mockResolvedValue(spec());
    vi.spyOn(api, "oneAppStatus").mockResolvedValue(status());
    const view = vi.spyOn(api, "providerView").mockImplementation(async (_key, id) =>
      id === "playlists" ? [{ id: "PL1", title: "Focus music", subtitle: "12 videos", url: "https://www.youtube.com/playlist?list=PL1", badge: null, image: null, drill: "playlist-items", body: null }]
        : [{ id: "v1", title: "Song one", subtitle: "Artist", url: null, badge: null, image: null, drill: null, body: null }]);
    render(<ProviderAppWorkspace appKey="youtube" />);
    expect(await screen.findByRole("tab", { name: "Playlists" })).toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: "Playlist" })).not.toBeInTheDocument(); // hidden views are not tabs
    expect(await screen.findByText("Focus music")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Open" }));
    expect(await screen.findByText("Song one")).toBeInTheDocument();
    expect(view).toHaveBeenCalledWith("youtube", "playlist-items", { arg: "PL1" });
  });

  it("waits for required input before searching, then shows results", async () => {
    vi.spyOn(api, "providerSpec").mockResolvedValue(spec());
    vi.spyOn(api, "oneAppStatus").mockResolvedValue(status());
    const view = vi.spyOn(api, "providerView").mockImplementation(async (_k, id) => (id === "search" ? [{ id: "x", title: "Result", subtitle: null, url: null, badge: "1k views", image: null, drill: null, body: null }] : []));
    render(<ProviderAppWorkspace appKey="youtube" />);
    fireEvent.click(await screen.findByRole("tab", { name: "Search" }));
    expect(await screen.findByText(/Fill in the details above/)).toBeInTheDocument();
    expect(view).not.toHaveBeenCalledWith("youtube", "search", expect.anything());
    fireEvent.change(screen.getByLabelText("Search YouTube"), { target: { value: "lofi" } });
    fireEvent.click(screen.getByRole("button", { name: "Show" }));
    expect(await screen.findByText("Result")).toBeInTheDocument();
    expect(view).toHaveBeenCalledWith("youtube", "search", { q: "lofi" });
  });

  it("keeps actions switched off until full access is on, and runs them after", async () => {
    vi.spyOn(api, "providerSpec").mockResolvedValue(spec());
    const st = vi.spyOn(api, "oneAppStatus").mockResolvedValue(status({ write: false }));
    vi.spyOn(api, "providerView").mockResolvedValue([]);
    const run = vi.spyOn(api, "providerAction").mockResolvedValue({ message: "Playlist created.", url: "https://www.youtube.com/playlist?list=PL9" });
    const { unmount } = render(<ProviderAppWorkspace appKey="youtube" />);
    fireEvent.click(await screen.findByRole("tab", { name: "Do something" }));
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Road trip" } });
    expect(screen.getByRole("button", { name: "Create a playlist" })).toBeDisabled();
    expect(screen.getByText(/read-only access/)).toBeInTheDocument();
    unmount();
    st.mockResolvedValue(status({ write: true }));
    render(<ProviderAppWorkspace appKey="youtube" />);
    fireEvent.click(await screen.findByRole("tab", { name: "Do something" }));
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Road trip" } });
    fireEvent.click(screen.getByRole("button", { name: "Create a playlist" }));
    expect(await screen.findByText(/Playlist created\./)).toBeInTheDocument();
    expect(run).toHaveBeenCalledWith("youtube", "create-playlist", { title: "Road trip" });
  });

  it("only turns remote image links into pictures and web links into anchors", async () => {
    vi.spyOn(api, "providerSpec").mockResolvedValue(spec());
    vi.spyOn(api, "oneAppStatus").mockResolvedValue(status());
    vi.spyOn(api, "providerView").mockResolvedValue([
      { id: "a", title: "Safe", subtitle: null, url: "https://example.com/a", badge: null, image: "https://img.example.com/a.jpg", drill: null, body: null },
      { id: "b", title: "Unsafe", subtitle: null, url: "javascript:alert(1)", badge: null, image: "javascript:alert(1)", drill: null, body: null },
    ]);
    const { container } = render(<ProviderAppWorkspace appKey="youtube" />);
    await screen.findByText("Safe");
    expect(screen.getByText("Safe").closest("a")).toHaveAttribute("href", "https://example.com/a");
    expect(screen.getByText("Unsafe").closest("a")).toBeNull();
    expect(container.querySelectorAll("img")).toHaveLength(1);
  });
});
