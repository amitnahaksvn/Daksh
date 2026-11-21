"use client";
import Link from "next/link";
import type { IconType } from "react-icons";
import { SiAmazon, SiBigbasket, SiFlipkart } from "@/components/brandLogos";
import {
  SiAirtable,
  SiApple,
  SiBitbucket,
  SiBluesky,
  SiBookingdotcom,
  SiBox,
  SiCalendly,
  SiCloudflare,
  SiConfluence,
  SiDiscord,
  SiDocker,
  SiDropbox,
  SiEbay,
  SiFacebook,
  SiFirebase,
  SiGithub,
  SiGitlab,
  SiGmail,
  SiGooglecalendar,
  SiGooglechat,
  SiGooglecloud,
  SiGoogledocs,
  SiGoogledrive,
  SiGooglemaps,
  SiGooglesheets,
  SiGoogleslides,
  SiHomeassistant,
  SiIfttt,
  SiImessage,
  SiInstagram,
  SiJira,
  SiKubernetes,
  SiLine,
  SiMake,
  SiMapbox,
  SiMastodon,
  SiMatrix,
  SiMattermost,
  SiMessenger,
  SiNotion,
  SiPaypal,
  SiPhilipshue,
  SiPinterest,
  SiProtonmail,
  SiQuickbooks,
  SiRazorpay,
  SiReddit,
  SiSentry,
  SiShopify,
  SiSignal,
  SiSmartthings,
  SiStripe,
  SiSupabase,
  SiTelegram,
  SiTiktok,
  SiTwitch,
  SiUber,
  SiVercel,
  SiWhatsapp,
  SiWoocommerce,
  SiX,
  SiXero,
  SiYoutube,
  SiZapier,
  SiSwiggy,
  SiZomato,
  SiZoho,
  SiZoom,
} from "react-icons/si";
import { Chip, buttonClass, secondaryButtonClass, tileClass } from "@/components/ui";
import type { AppCatalogEntry, ConnectionStatusValue, DashboardApp } from "@/lib/api";

// Maps the backend's "icon" string (a react-icons/si export name, or "mono:<text>" for the handful of
// brands withdrawn from open icon sets — see docs on the Apps page build) to the actual component.
const BRAND_ICONS: Record<string, IconType> = {
  SiAmazon,
  SiFlipkart,
  SiBigbasket,
  SiSwiggy,
  SiZomato,
  SiWhatsapp,
  SiTelegram,
  SiDiscord,
  SiGooglechat,
  SiSignal,
  SiLine,
  SiMessenger,
  SiInstagram,
  SiImessage,
  SiMatrix,
  SiMattermost,
  SiTwitch,
  SiGmail,
  SiZoho,
  SiProtonmail,
  SiGooglecalendar,
  SiCalendly,
  SiZoom,
  SiGoogledrive,
  SiDropbox,
  SiBox,
  SiGoogledocs,
  SiGooglesheets,
  SiGoogleslides,
  SiNotion,
  SiConfluence,
  SiAirtable,
  SiFacebook,
  SiX,
  SiYoutube,
  SiTiktok,
  SiReddit,
  SiPinterest,
  SiMastodon,
  SiBluesky,
  SiGithub,
  SiGitlab,
  SiBitbucket,
  SiJira,
  SiSentry,
  SiDocker,
  SiKubernetes,
  SiGooglecloud,
  SiCloudflare,
  SiVercel,
  SiSupabase,
  SiFirebase,
  SiShopify,
  SiWoocommerce,
  SiEbay,
  SiRazorpay,
  SiStripe,
  SiPaypal,
  SiQuickbooks,
  SiXero,
  SiGooglemaps,
  SiMapbox,
  SiBookingdotcom,
  SiUber,
  SiHomeassistant,
  SiPhilipshue,
  SiSmartthings,
  SiIfttt,
  SiZapier,
  SiMake,
  SiApple,
};

export function BrandIcon({ icon, color, size = 26 }: { icon: string; color: string; size?: number }) {
  if (icon.startsWith("mono:")) {
    return (
      <span className="text-sm font-extrabold" style={{ color }}>
        {icon.slice(5)}
      </span>
    );
  }
  // A brand with its own full-colour logo file (for example Zepto's wordmark): "img:/brands/zepto.svg".
  if (icon.startsWith("img:")) {
    // eslint-disable-next-line @next/next/no-img-element
    return <img src={icon.slice(4)} alt="" width={Math.round(size * 1.6)} height={size} style={{ width: "88%", height: "auto", maxHeight: "70%", objectFit: "contain" }} />;
  }
  const Icon = BRAND_ICONS[icon];
  return Icon ? <Icon size={size} color={color} /> : null;
}

export function BrandTile({ icon, color, size = 48, highlighted = false }: { icon: string; color: string; size?: number; highlighted?: boolean }) {
  return (
    <div
      className={`flex shrink-0 items-center justify-center rounded-2xl bg-white shadow-[var(--shadow)] ${highlighted ? "ring-2 ring-accent ring-offset-2 ring-offset-canvas" : ""}`}
      style={{ width: size, height: size }}
    >
      <BrandIcon icon={icon} color={color} size={Math.round(size * 0.55)} />
    </div>
  );
}

const PRIORITY_TONE: Record<"High" | "Medium" | "Low", { bg: string; text: string }> = {
  High: { bg: "bg-pink", text: "text-on-tint" },
  Medium: { bg: "bg-[color-mix(in_srgb,var(--lime)_55%,orange)]", text: "text-on-tint" },
  Low: { bg: "bg-surface-2", text: "text-muted" },
};

/** Build-roadmap priority — not a connection status. Static, honest metadata about what we're building next. */
export function PriorityBadge({ priority }: { priority: "High" | "Medium" | "Low" }) {
  const tone = PRIORITY_TONE[priority];
  return (
    <span className={`ai-mono inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-semibold ${tone.bg} ${tone.text}`}>
      <svg viewBox="0 0 16 16" fill="currentColor" className="h-3 w-3">
        <path d="M9.5 1 3 9h4l-1 6 6.5-8H8.5z" />
      </svg>
      {priority}
    </span>
  );
}

const STATUS_LABEL: Record<ConnectionStatusValue, string> = {
  NotConnected: "Not connected",
  Connecting: "Connecting…",
  Connected: "Connected",
  Disconnected: "Disconnected",
  Error: "Error",
};
const STATUS_TONE: Record<ConnectionStatusValue, "lime" | "pink" | "plain"> = {
  NotConnected: "plain",
  Connecting: "pink",
  Connected: "lime",
  Disconnected: "plain",
  Error: "pink",
};

export function StatusChip({ status }: { status: ConnectionStatusValue }) {
  return <Chip tone={STATUS_TONE[status]}>{STATUS_LABEL[status]}</Chip>;
}

export function ComingSoonChip() {
  return <Chip tone="plain">Coming soon</Chip>;
}

/** One app tile on the Apps page: real status + live "Configure"/"Manage" for WhatsApp, "Coming soon" for the rest. */
export function AppCard({
  app,
  priority,
  description,
  status,
  onConfigure,
}: {
  app: AppCatalogEntry;
  priority: "High" | "Medium" | "Low";
  description: string;
  status?: ConnectionStatusValue;
  /** When given, "Configure"/"Manage" calls this (opens a modal) instead of navigating to /apps/{key}. */
  onConfigure?: () => void;
}) {
  const isWhatsApp = app.integrationKey === "whatsapp";
  const actionLabel = status === "Connected" ? "Manage" : "Configure";
  const actionContent = actionLabel;
  return (
    <li
      className={`flex flex-col gap-3 p-5 ${tileClass} ${isWhatsApp ? "border-accent ring-1 ring-accent" : ""}`}
    >
      <div className="flex items-start justify-between gap-2">
        <BrandTile icon={app.icon} color={app.color} />
        <PriorityBadge priority={priority} />
      </div>
      <div>
        <p className="font-semibold">{app.name}</p>
        <p className="mt-0.5 text-sm text-muted">{description}</p>
      </div>
      <div className="mt-auto flex items-center justify-between gap-2 pt-1">
        {app.isEnabled && status ? <StatusChip status={status} /> : <ComingSoonChip />}
        {app.isEnabled ? (
          onConfigure ? (
            <button onClick={onConfigure} className={`${buttonClass} gap-1 px-4 py-2 text-xs`}>{actionContent}</button>
          ) : (
            <Link href={`/apps/${app.integrationKey}`} className={`${buttonClass} gap-1 px-4 py-2 text-xs`}>{actionContent}</Link>
          )
        ) : (
          <span className="rounded-full border border-line px-4 py-2 text-xs font-semibold text-muted opacity-60">Configure</span>
        )}
      </div>
    </li>
  );
}

export function DashboardAppCard({ app, onRemove }: { app: DashboardApp; onRemove: (id: string) => void }) {
  return (
    <div className={`flex min-h-full flex-wrap items-center gap-4 p-5 ${tileClass}`}>
      <div className="relative shrink-0">
        <BrandTile icon={app.icon} color={app.color} size={44} />
        {app.unreadCount > 0 && (
          <span className="absolute -right-1 -top-1 flex h-4.5 min-w-[1.125rem] items-center justify-center rounded-full bg-pink px-1 text-[10px] font-bold text-on-tint">
            {app.unreadCount > 9 ? "9+" : app.unreadCount}
          </span>
        )}
      </div>
      <div className="min-w-[10rem] flex-1">
        <p className="font-semibold">{app.name}</p>
        <div className="mt-1 flex items-center gap-2">
          <StatusChip status={app.status} />
          {app.accountIdentifier && <span className="truncate text-xs text-muted">{app.accountIdentifier}</span>}
        </div>
        {app.lastMessagePreview ? (
          <p className="mt-1 truncate text-xs text-muted">
            {app.lastMessagePreview}
            {app.lastMessageAt && ` · ${new Date(app.lastMessageAt).toLocaleString()}`}
          </p>
        ) : (
          app.lastActiveAt && <p className="mt-1 text-xs text-muted">Last active {new Date(app.lastActiveAt).toLocaleString()}</p>
        )}
      </div>
      <div className="flex shrink-0 flex-col gap-2 sm:flex-row">
        <Link href={`/apps/${app.integrationKey}`} className={`${secondaryButtonClass} px-4 py-2 text-xs`}>Manage</Link>
        <button onClick={() => onRemove(app.id)} className={`${secondaryButtonClass} px-4 py-2 text-xs`}>Remove</button>
      </div>
    </div>
  );
}
