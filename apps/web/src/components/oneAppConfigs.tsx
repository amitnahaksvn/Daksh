"use client";
import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { OneAppManagePanel } from "@/components/OneAppManagePanel";
import type { OneAppConfig } from "@/components/OneAppParts";
import { buttonClass, inputClass, tileClass } from "@/components/ui";
import { api, ApiError, type OneAppKey } from "@/lib/api";

/** Discord asks for a bot token the person creates themselves (nothing to configure on the server). */
function DiscordConnectCard({ onConnected, replacing = false }: { onConnected: () => void; replacing?: boolean }) {
  const [token, setToken] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save() {
    setBusy(true); setError(null);
    try { await api.connectDiscord(token); setToken(""); onConnected(); }
    catch (err) { setError(err instanceof ApiError ? err.message : "Could not connect the bot."); }
    finally { setBusy(false); }
  }
  return (
    <div className={`p-5 ${tileClass}`}>
      <h3 className="text-lg font-bold">{replacing ? "Use a different bot token" : "Connect a Discord bot"}</h3>
      <ol className="mt-2 list-decimal space-y-1 pl-5 text-sm text-muted">
        <li>Open the <a className="font-semibold text-accent underline" href="https://discord.com/developers/applications" target="_blank" rel="noreferrer">Discord Developer Portal</a> and make a New Application.</li>
        <li>On the <strong>Bot</strong> page, turn on <strong>Message Content Intent</strong>, then press <strong>Reset Token</strong> and copy it.</li>
        <li>Paste the token below. Daksha then gives you a link to invite the bot to your servers.</li>
      </ol>
      <input value={token} onChange={(e) => setToken(e.target.value)} type="password" placeholder="Bot token" aria-label="Discord bot token" autoComplete="off" className={`${inputClass} mt-3`} />
      <p className="mt-1 text-xs text-muted">The token is stored encrypted and never shown again. Daksha reads and posts only where the bot was invited.</p>
      {error && <p role="alert" className="mt-3 text-sm text-danger">{error}</p>}
      <button type="button" onClick={() => void save()} disabled={busy || !token.trim()} className={`${buttonClass} mt-3 px-5`}>{busy ? "Checking…" : "Connect bot"}</button>
    </div>
  );
}

function DiscordExtras({ reload }: { reload: () => void }) {
  const [invite, setInvite] = useState<string | null | undefined>(undefined);
  const [replace, setReplace] = useState(false);
  useEffect(() => { api.discordInvite().then((i) => setInvite(i.url), () => setInvite(null)); }, []);
  return (
    <section>
      <h3 className="text-base font-bold">Bot</h3>
      <p className="mb-2 text-xs text-muted">The bot only sees servers it was invited to. Give it permission to view channels, read message history and send messages.</p>
      <div className="flex flex-wrap gap-2 rounded-[1.25rem] border border-line p-3">
        {invite && <a href={invite} target="_blank" rel="noreferrer" className={`${buttonClass} px-4`}>Invite the bot to a server</a>}
        <button type="button" onClick={() => setReplace((r) => !r)} className="rounded-full border border-line px-4 py-2 text-sm font-semibold hover:bg-surface-2">{replace ? "Cancel" : "Use a different token"}</button>
      </div>
      {replace && <div className="mt-3"><DiscordConnectCard replacing onConnected={() => { setReplace(false); reload(); }} /></div>}
    </section>
  );
}

function renderDiscordConnect(onConnected: () => void): ReactNode { return <DiscordConnectCard onConnected={onConnected} />; }
function renderDiscordExtras(_status: unknown, reload: () => void): ReactNode { return <DiscordExtras reload={reload} />; }

const WATCH_HINT = "Channel IDs, separated by commas (up to 5). In Discord turn on Developer Mode, then right-click a channel and choose Copy Channel ID.";

export const ONE_APPS: Record<OneAppKey, OneAppConfig> = {
  github: {
    key: "github", name: "GitHub", icon: "SiGithub", color: "#181717", pageHref: "/apps/github", account: "Pull requests, issues and notifications", setupDoc: "docs/integrations/github.md",
    connectTitle: "Connect your GitHub account", connectLabel: "Continue with GitHub",
    connectBlurb: "Sign in with GitHub and Daksha can show the pull requests waiting for your review, your issues and notifications, and answer questions about them. It never sees your GitHub password.",
    writeLabel: "Allow Daksh to comment", writeHint: "Full access lets Daksha comment on issues and pull requests and mark notifications read.", writeNeedsSignIn: true,
    options: [{ id: "private", label: "Include private repositories", hint: "GitHub has no read-only option for private repositories, so this asks for the repo permission.", granted: (s) => (s.scopes ?? "").split(" ").includes("repo") }],
    tabs: [
      { id: "work", label: "Work", sections: [{ title: "Dashboard", hint: "What the GitHub tile on your dashboard shows.", rows: [
        { key: "dashboardSource", label: "Which items", type: "enum", options: [{ value: "review", label: "Review requested" }, { value: "assigned", label: "Assigned to me" }, { value: "created", label: "Created by me" }, { value: "mentioned", label: "Mentioning me" }] },
        { key: "itemCount", label: "Items to show", type: "count", noun: "items" },
        { key: "includeIssues", label: "Include issues", hint: "Off: only pull requests.", type: "bool" },
        { key: "hideDrafts", label: "Hide draft pull requests", type: "bool" },
      ] }] },
      { id: "notifications", label: "Notifications", sections: [{ title: "Notifications", rows: [
        { key: "includeNotifications", label: "Show notifications on the dashboard", hint: "Your unread GitHub notifications.", type: "bool" },
      ] }] },
    ],
  },
  notion: {
    key: "notion", name: "Notion", icon: "SiNotion", color: "#000000", pageHref: "/apps/notion", account: "Pages and databases", setupDoc: "docs/integrations/notion.md",
    connectTitle: "Connect your Notion workspace", connectLabel: "Continue with Notion",
    connectBlurb: "Sign in with Notion and choose which pages and databases to share with Daksha. It sees only what you share, and never your Notion password.",
    writeLabel: "Allow Daksh to create and edit pages", writeHint: "Full access lets Daksha create pages and add text. It also needs the Insert and Update content capabilities on your Notion integration.", writeNeedsSignIn: false,
    tabs: [
      { id: "pages", label: "Pages", sections: [{ title: "Reading", rows: [
        { key: "textLimit", label: "Longest page to read", hint: "How many characters of a page Daksha reads at once.", type: "enum", options: [{ value: "5000", label: "5,000 characters" }, { value: "20000", label: "20,000 characters" }, { value: "50000", label: "50,000 characters" }] },
      ] }] },
      { id: "dashboard", label: "Dashboard", sections: [{ title: "Dashboard tile", rows: [
        { key: "dashboardSource", label: "What to show", type: "enum", options: [{ value: "pages", label: "Recently edited pages" }, { value: "databases", label: "Databases" }] },
        { key: "itemCount", label: "Items to show", type: "count", noun: "items" },
      ] }] },
    ],
  },
  discord: {
    key: "discord", name: "Discord", icon: "SiDiscord", color: "#5865F2", pageHref: "/apps/discord", account: "Servers and channels", setupDoc: "docs/integrations/discord.md",
    connectTitle: "Connect a Discord bot", connectLabel: "Connect bot",
    connectBlurb: "Create your own Discord bot and paste its token.",
    writeLabel: "Allow Daksh to post messages", writeHint: "Full access lets Daksha post in channels as the bot.", writeNeedsSignIn: false,
    connect: renderDiscordConnect,
    extraGeneral: renderDiscordExtras,
    tabs: [
      { id: "messages", label: "Messages", sections: [{ title: "Reading", rows: [
        { key: "hideBots", label: "Hide messages from bots", hint: "Leaves out automated posts.", type: "bool" },
      ] }] },
      { id: "dashboard", label: "Dashboard", sections: [{ title: "Dashboard tile", hint: "The tile shows the latest messages in the channels you watch, or your servers when none are chosen.", rows: [
        { key: "watchedChannels", label: "Channels to watch", hint: WATCH_HINT, type: "text", placeholder: "123456789012345678, …" },
        { key: "itemCount", label: "Messages per channel", type: "count", noun: "messages" },
      ] }] },
    ],
  },
  spotify: {
    key: "spotify", name: "Spotify", icon: "mono:SP", color: "#1DB954", pageHref: "/apps/spotify", account: "Now playing, playlists and history", setupDoc: "docs/integrations/spotify.md",
    connectTitle: "Connect your Spotify account", connectLabel: "Continue with Spotify",
    connectBlurb: "Sign in with Spotify and Daksha can show what is playing, your recent plays and playlists, and search. It never sees your Spotify password.",
    writeLabel: "Allow Daksh to control playback", writeHint: "Full access lets Daksha play, pause, skip, queue songs and play playlists. Spotify only allows playback control on Premium.", writeNeedsSignIn: true,
    tabs: [
      { id: "listening", label: "Listening", sections: [{ title: "Top tracks", rows: [
        { key: "topRange", label: "Top tracks over", type: "enum", options: [{ value: "short_term", label: "The last 4 weeks" }, { value: "medium_term", label: "The last 6 months" }, { value: "long_term", label: "All time" }] },
      ] }] },
      { id: "dashboard", label: "Dashboard", sections: [{ title: "Dashboard tile", rows: [
        { key: "showRecent", label: "Show recently played", type: "bool" },
        { key: "itemCount", label: "Tracks to show", type: "count", noun: "tracks" },
      ] }] },
    ],
  },
};

function ManagePanelFor({ appKey, onChanged }: { appKey: OneAppKey; onChanged: () => void }) {
  return <OneAppManagePanel config={ONE_APPS[appKey]} onChanged={onChanged} />;
}

/** The Manage panel renderer for an app, used by its page and its dashboard tile. */
export function manageFor(key: OneAppKey) {
  return function renderManage(onChanged: () => void): ReactNode { return <ManagePanelFor appKey={key} onChanged={onChanged} />; };
}
