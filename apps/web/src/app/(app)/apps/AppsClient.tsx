"use client";
import { useRouter } from "next/navigation";
import { useMemo, useState } from "react";
import { AppCard, BrandTile, StatusChip } from "@/components/integrations";
import { Modal } from "@/components/Modal";
import { ErrorState, Loading, inputClass, tileClass } from "@/components/ui";
import { GmailManagePanel } from "@/components/GmailManagePanel";
import { WhatsAppConnectPanel } from "@/components/WhatsAppConnectPanel";
import { TelegramConnectPanel } from "@/components/TelegramConnectPanel";
import { MicrosoftManagePanel } from "@/components/MicrosoftManagePanel";
import { GoogleWorkspaceManagePanel } from "@/components/GoogleWorkspaceManagePanel";
import { SlackManagePanel } from "@/components/SlackManagePanel";
import { OneAppManagePanel } from "@/components/OneAppManagePanel";
import { ONE_APPS } from "@/components/oneAppConfigs";
import { ShoppingPanel } from "@/components/ShoppingPanel";
import { microsoftCallbackError } from "@/components/MicrosoftParts";
import { api, type AppCatalogEntry, type ConnectionStatusValue, type OneAppKey } from "@/lib/api";
import { useApi } from "@/lib/useApi";


type Priority = "High" | "Medium" | "Low";

// Build-roadmap metadata (priority + a one-line description of what each app does) — not a connection
// status, which is always real and comes from the API (see components/integrations.tsx's AppCard).
const APP_META: Record<string, { priority: Priority; description: string }> = {
  whatsapp: { priority: "High", description: "Connect your WhatsApp account to chat, read and send messages." },
  telegram: { priority: "High", description: "Link a Telegram bot to receive and send messages for you." },
  slack: { priority: "High", description: "Bring Slack conversations and notifications into Daksh." },
  "microsoft-teams": { priority: "High", description: "Read and reply to Microsoft Teams chats from one place." },
  discord: { priority: "High", description: "Connect a Discord bot to manage servers and DMs." },
  "google-chat": { priority: "Medium", description: "Sync Google Chat spaces and direct messages." },
  signal: { priority: "Medium", description: "Bridge Signal messages through a linked device." },
  line: { priority: "Medium", description: "Connect a LINE Official Account for messaging." },
  "facebook-messenger": { priority: "Medium", description: "Reply to Facebook Page messages from Daksh." },
  "instagram-messaging": { priority: "Medium", description: "Manage Instagram DMs for a connected business account." },
  imessage: { priority: "Low", description: "Bridge iMessage conversations from a Mac on your network." },
  matrix: { priority: "Medium", description: "Join Matrix rooms and relay messages both ways." },
  mattermost: { priority: "Medium", description: "Connect a Mattermost workspace for team messaging." },
  "feishu-lark": { priority: "Medium", description: "Bring Feishu/Lark messages and approvals into Daksh." },
  "twitch-chat": { priority: "Low", description: "Read and post to a Twitch channel's chat." },
  "sms-twilio": { priority: "Medium", description: "Send and receive SMS through a Twilio number." },
  gmail: { priority: "High", description: "Read, search and send email from your Gmail inbox." },
  amazon: { priority: "Medium", description: "Sign in once, then say what you want and Daksha adds it to your Amazon cart. You check the cart and pay on Amazon yourself." },
  flipkart: { priority: "Medium", description: "Sign in once, then say what you want and Daksha adds it to your Flipkart cart. You check the cart and pay on Flipkart yourself." },
  "microsoft-outlook": { priority: "High", description: "Read, search and send Outlook email. One Microsoft sign-in also covers Calendar, OneDrive and Azure DevOps." },
  "yahoo-mail": { priority: "Low", description: "Connect a Yahoo Mail inbox via IMAP/SMTP." },
  "zoho-mail": { priority: "Low", description: "Connect Zoho Mail for a business or personal domain." },
  "proton-mail": { priority: "Low", description: "Bridge a Proton Mail inbox for reading and sending." },

  "google-calendar": { priority: "Medium", description: "See, add, edit and delete events across all your Google accounts. Uses the same Google sign-in as Gmail." },
  "microsoft-outlook-calendar": { priority: "Medium", description: "See and add Outlook events, shown in Calendar next to Google. Uses the same Microsoft sign-in." },
  "apple-calendar": { priority: "Low", description: "Sync events with Apple Calendar via a local bridge." },
  calendly: { priority: "Low", description: "Pull booked meetings and availability from Calendly." },

  zoom: { priority: "Low", description: "Schedule, join and manage Zoom meetings." },

  "google-drive": { priority: "Low", description: "Browse, read and write files in a connected Google Drive." },
  onedrive: { priority: "Medium", description: "Browse and search your OneDrive files. Uses the same Microsoft sign-in." },
  sharepoint: { priority: "Low", description: "Read and manage documents in a SharePoint site." },
  dropbox: { priority: "Low", description: "Browse, read and write files in a connected Dropbox." },
  box: { priority: "Low", description: "Browse, read and write files in a connected Box account." },

  "google-docs": { priority: "Low", description: "Read and edit Google Docs documents." },
  "google-sheets": { priority: "Low", description: "Read and edit Google Sheets spreadsheets." },
  "google-slides": { priority: "Low", description: "Read and edit Google Slides presentations." },

  notion: { priority: "Low", description: "Read and write pages and databases in a connected Notion workspace." },
  confluence: { priority: "Low", description: "Read and write pages in a connected Confluence space." },

  airtable: { priority: "Low", description: "Read and write records in a connected Airtable base." },

  "facebook-pages": { priority: "Low", description: "Post and read comments on a managed Facebook Page." },
  "instagram-social": { priority: "Low", description: "Post and read comments on a connected Instagram business account." },
  x: { priority: "Low", description: "Post and read replies on a connected X account." },
  linkedin: { priority: "Low", description: "Post and read activity on a connected LinkedIn profile or page." },
  youtube: { priority: "Low", description: "Read comments and manage uploads on a connected YouTube channel." },
  tiktok: { priority: "Low", description: "Post and read comments on a connected TikTok account." },
  reddit: { priority: "Low", description: "Read and post to subreddits from a connected Reddit account." },
  pinterest: { priority: "Low", description: "Post and read activity on a connected Pinterest account." },
  mastodon: { priority: "Low", description: "Post and read activity on a connected Mastodon account." },
  bluesky: { priority: "Low", description: "Post and read activity on a connected Bluesky account." },

  github: { priority: "Low", description: "Read issues, PRs and repos on a connected GitHub account." },
  gitlab: { priority: "Low", description: "Read issues, MRs and repos on a connected GitLab account." },
  bitbucket: { priority: "Low", description: "Read repos and pull requests on a connected Bitbucket account." },
  "azure-devops": { priority: "Medium", description: "See the work items assigned to you. Uses the same Microsoft sign-in (work or school account)." },
  jira: { priority: "Low", description: "Read and update issues in a connected Jira project." },
  sentry: { priority: "Low", description: "Read error events and issues from a connected Sentry project." },
  "docker-hub": { priority: "Low", description: "Browse images and tags on a connected Docker Hub account." },
  kubernetes: { priority: "Low", description: "Read cluster and workload status from a connected Kubernetes context." },

  aws: { priority: "Low", description: "Read and manage resources on a connected AWS account." },
  "microsoft-azure": { priority: "Low", description: "Read and manage resources on a connected Azure subscription." },
  "google-cloud": { priority: "Low", description: "Read and manage resources on a connected Google Cloud project." },
  cloudflare: { priority: "Low", description: "Manage DNS, caching and Workers on a connected Cloudflare account." },
  vercel: { priority: "Low", description: "Read deployments and manage projects on a connected Vercel account." },
  supabase: { priority: "Low", description: "Read and manage data on a connected Supabase project." },
  firebase: { priority: "Low", description: "Read and manage data on a connected Firebase project." },

  shopify: { priority: "Low", description: "Read orders and manage products on a connected Shopify store." },
  woocommerce: { priority: "Low", description: "Read orders and manage products on a connected WooCommerce store." },
  ebay: { priority: "Low", description: "Read orders and manage listings on a connected eBay account." },
  "amazon-selling-partner": { priority: "Low", description: "Read orders and manage listings via Amazon's Selling Partner API." },

  razorpay: { priority: "Low", description: "Read payments and manage orders on a connected Razorpay account." },
  stripe: { priority: "Low", description: "Read payments and manage customers on a connected Stripe account." },
  paypal: { priority: "Low", description: "Read payments and transactions on a connected PayPal account." },

  "quickbooks-online": { priority: "Low", description: "Read and write invoices and transactions in QuickBooks Online." },
  xero: { priority: "Low", description: "Read and write invoices and transactions in Xero." },
  "zoho-books": { priority: "Low", description: "Read and write invoices and transactions in Zoho Books." },

  "google-maps-platform": { priority: "Low", description: "Geocode addresses and compute routes via Google Maps Platform." },
  mapbox: { priority: "Low", description: "Geocode addresses and render maps via Mapbox." },

  amadeus: { priority: "Low", description: "Search flights and travel content via the Amadeus API." },
  "booking-com": { priority: "Low", description: "Search and manage stays via a connected Booking.com account." },

  uber: { priority: "Low", description: "Request and track rides via a connected Uber account." },

  irctc: { priority: "Low", description: "Check train availability and bookings via IRCTC." },

  digilocker: { priority: "Low", description: "Fetch verified documents from a connected DigiLocker account." },

  bhashini: { priority: "Low", description: "Translate and transcribe Indian languages via Bhashini." },

  ondc: { priority: "Low", description: "Browse and transact on ONDC network sellers." },

  "home-assistant": { priority: "Low", description: "Read and control devices on a connected Home Assistant instance." },
  "philips-hue": { priority: "Low", description: "Control lights and scenes on a connected Philips Hue bridge." },
  smartthings: { priority: "Low", description: "Read and control devices on a connected SmartThings hub." },
  tuya: { priority: "Low", description: "Read and control devices on a connected Tuya account." },

  ifttt: { priority: "Low", description: "Trigger and react to IFTTT applets." },
  zapier: { priority: "Low", description: "Trigger and react to Zapier workflows." },
  n8n: { priority: "Low", description: "Trigger and react to workflows in a connected n8n instance." },
  make: { priority: "Low", description: "Trigger and react to scenarios in a connected Make instance." },

  "mcp-servers": { priority: "Low", description: "Connect Daksh to tools exposed by external MCP servers." },

  "openclaw-plugins": { priority: "Low", description: "Extend Daksh with community OpenClaw plugins." },

  "a2a": { priority: "Low", description: "Talk to other agents over the Agent-to-Agent protocol." },
  "buzz": { priority: "Low", description: "Chat with Daksh through Buzz." },
  "clickclack": { priority: "Low", description: "Chat with Daksh through ClickClack." },
  "irc": { priority: "Low", description: "Chat with Daksh from IRC channels and direct messages." },
  "nextcloud-talk": { priority: "Low", description: "Chat with Daksh from Nextcloud Talk rooms." },
  "nostr": { priority: "Low", description: "Chat with Daksh over Nostr direct messages." },
  "raft": { priority: "Low", description: "Chat with Daksh through Raft." },
  "reef": { priority: "Low", description: "Chat with Daksh through Reef." },
  "synology-chat": { priority: "Low", description: "Chat with Daksh from Synology Chat." },
  "tlon": { priority: "Low", description: "Chat with Daksh through Tlon." },
  "webchat": { priority: "Low", description: "Embed a web chat widget for Daksh." },
  "apple-notes": { priority: "Low", description: "Search and add notes in Apple Notes." },
  "obsidian": { priority: "Low", description: "Search and add notes in an Obsidian vault." },
  "spotify": { priority: "Low", description: "Control Spotify playback and playlists." },
  "sonos": { priority: "Low", description: "Control Sonos speakers and rooms." },
  "1password": { priority: "Low", description: "Look up items in 1Password without exposing secrets to the chat." },
  "weather": { priority: "Low", description: "Get forecasts and weather alerts for your places." },
  "anthropic": { priority: "Low", description: "Use Claude models." },
  "openai": { priority: "Low", description: "Use OpenAI models." },
  "google-gemini": { priority: "Low", description: "Use Google Gemini models." },
  "mistral": { priority: "Low", description: "Use Mistral models." },
  "deepseek": { priority: "Low", description: "Use DeepSeek models." },
  "qwen": { priority: "Low", description: "Use Qwen models." },
  "moonshot-kimi": { priority: "Low", description: "Use Moonshot Kimi models." },
  "minimax": { priority: "Low", description: "Use MiniMax models." },
  "zai": { priority: "Low", description: "Use Z.AI GLM models." },
  "cohere": { priority: "Low", description: "Use Cohere models." },
  "groq": { priority: "Low", description: "Run models on Groq." },
  "cerebras": { priority: "Low", description: "Run models on Cerebras." },
  "together-ai": { priority: "Low", description: "Run open models on Together AI." },
  "fireworks": { priority: "Low", description: "Run open models on Fireworks." },
  "nvidia": { priority: "Low", description: "Use NVIDIA-hosted models." },
  "hugging-face": { priority: "Low", description: "Use Hugging Face inference models." },
  "openrouter": { priority: "Low", description: "Route to many models through OpenRouter." },
  "litellm": { priority: "Low", description: "Route models through a LiteLLM proxy." },
  "amazon-bedrock": { priority: "Low", description: "Use models on Amazon Bedrock." },
  "anthropic-vertex": { priority: "Low", description: "Use Claude on Google Vertex AI." },
  "github-copilot": { priority: "Low", description: "Use models through GitHub Copilot." },
  "cloudflare-ai-gateway": { priority: "Low", description: "Route models through Cloudflare AI Gateway." },
  "vercel-ai-gateway": { priority: "Low", description: "Route models through Vercel AI Gateway." },
  "ollama": { priority: "Low", description: "Use local models served by Ollama." },
  "lm-studio": { priority: "Low", description: "Use local models served by LM Studio." },
  "vllm": { priority: "Low", description: "Use models served by vLLM." },
  "custom-openai-api": { priority: "Low", description: "Use any OpenAI-compatible endpoint." },
  "elevenlabs": { priority: "Low", description: "Speech synthesis with ElevenLabs." },
  "deepgram": { priority: "Low", description: "Speech-to-text with Deepgram." },
  "azure-speech": { priority: "Low", description: "Speech services on Azure." },
  "perplexity-search": { priority: "Low", description: "Web search through Perplexity." },
  "runway": { priority: "Low", description: "Generate video with Runway." },
  "fal": { priority: "Low", description: "Generate images and media with fal." },
  "comfyui": { priority: "Low", description: "Run image workflows on ComfyUI." },
};

const CATEGORY_META: Record<string, { title: string; description: string; glyph: React.ReactNode }> = {
  Messaging: {
    title: "Messaging & Communication",
    description: "Chat platforms Daksh can read, reply to and automate.",
    glyph: (
      <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M2 3h12v8H5l-3 3V3Z" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>
    ),
  },
  Email: {
    title: "Email & Productivity",
    description: "Inboxes Daksh can search, read and send from.",
    glyph: (
      <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M2 4h12v8H2V4Zm0 0 6 5 6-5" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>
    ),
  },
  Shopping: {
    title: "Shopping",
    description: "Online stores. Sign in once, then add items to your cart by command.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M2 3h2l1.5 7h6.5l1.5-5H5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" /><circle cx="6.5" cy="13" r="1" stroke="currentColor" strokeWidth="1.1" /><circle cx="11.5" cy="13" r="1" stroke="currentColor" strokeWidth="1.1" /></svg>,
  },
  Calendar: {
    title: "Calendar",
    description: "Scheduling and events — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><rect x="2" y="3" width="12" height="11" rx="1" stroke="currentColor" strokeWidth="1.3" /><path d="M2 6.5h12M5 2v2.5M11 2v2.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>,
  },
  Meetings: {
    title: "Meetings",
    description: "Video calls — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><rect x="2" y="4" width="8" height="8" rx="1" stroke="currentColor" strokeWidth="1.3" /><path d="M10 6.5 14 4v8l-4-2.5" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>,
  },
  Storage: {
    title: "Storage",
    description: "Files and drives — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M2 9 4 3h8l2 6M2 9v4h12V9M2 9h12" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>,
  },
  Documents: {
    title: "Documents",
    description: "Docs, sheets and slides — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M4 2h6l3 3v9H4V2Z" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /><path d="M6 8h4M6 11h4" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>,
  },
  Knowledge: {
    title: "Knowledge",
    description: "Notes and wikis — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M3 3h6v10H3V3Zm6 0h4v10H9V3Z" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>,
  },
  Data: {
    title: "Data",
    description: "Structured records — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><rect x="2" y="2" width="12" height="12" rx="1" stroke="currentColor" strokeWidth="1.3" /><path d="M2 6.5h12M2 10.5h12M6.5 2v12" stroke="currentColor" strokeWidth="1.3" /></svg>,
  },
  Social: {
    title: "Social",
    description: "Posting and engagement — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><circle cx="5" cy="5" r="2" stroke="currentColor" strokeWidth="1.3" /><circle cx="11" cy="4" r="2" stroke="currentColor" strokeWidth="1.3" /><circle cx="8" cy="12" r="2" stroke="currentColor" strokeWidth="1.3" /><path d="M6.5 6 7 10.3M9.5 5.6 9 10.3" stroke="currentColor" strokeWidth="1.1" /></svg>,
  },
  Developer: {
    title: "Developer",
    description: "Code hosting and dev tools — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M5 4 1 8l4 4M11 4l4 4-4 4" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" /></svg>,
  },
  Cloud: {
    title: "Cloud",
    description: "Infrastructure and hosting — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M4.5 11.5a3 3 0 0 1-.5-5.96A3.5 3.5 0 0 1 11 5.1 2.8 2.8 0 0 1 10.8 11.5H4.5Z" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>,
  },
  Commerce: {
    title: "Commerce",
    description: "Storefronts and marketplaces — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M3 5h10l-1 8H4L3 5Zm2.5 0V4a2.5 2.5 0 0 1 5 0v1" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>,
  },
  Payments: {
    title: "Payments",
    description: "Transactions and billing — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><rect x="2" y="4" width="12" height="8" rx="1" stroke="currentColor" strokeWidth="1.3" /><path d="M2 7h12" stroke="currentColor" strokeWidth="1.3" /></svg>,
  },
  Accounting: {
    title: "Accounting",
    description: "Invoices and bookkeeping — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><rect x="3" y="2" width="10" height="12" rx="1" stroke="currentColor" strokeWidth="1.3" /><path d="M6 6h4M6 9h4M6 12h2" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>,
  },
  Maps: {
    title: "Maps",
    description: "Geocoding and routing — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M8 14s5-4.5 5-8a5 5 0 0 0-10 0c0 3.5 5 8 5 8Z" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /><circle cx="8" cy="6" r="1.5" stroke="currentColor" strokeWidth="1.1" /></svg>,
  },
  Travel: {
    title: "Travel",
    description: "Flights and stays — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M2 9.5 14 5l-3 4.5L14 11l-12-1.5Z" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /><path d="M6 9 5 13h2l1.5-2.5" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>,
  },
  Mobility: {
    title: "Mobility",
    description: "Rides and transport — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M3 10h10l-1.2-4.2a1 1 0 0 0-1-.8H5.2a1 1 0 0 0-1 .8L3 10Z" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /><circle cx="5" cy="11.5" r="1.2" stroke="currentColor" strokeWidth="1.1" /><circle cx="11" cy="11.5" r="1.2" stroke="currentColor" strokeWidth="1.1" /></svg>,
  },
  "India travel": {
    title: "India travel",
    description: "Rail and travel bookings in India — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><rect x="3" y="3" width="10" height="8" rx="2" stroke="currentColor" strokeWidth="1.3" /><path d="M3 7h10M6 13l-1.5 2M10 13l1.5 2" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>,
  },
  "India services": {
    title: "India services",
    description: "Government documents and services — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M4 2h6l3 3v9H4V2Z" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /><path d="M6.5 8.5 7.5 9.5 9.5 7" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" /></svg>,
  },
  "India AI": {
    title: "India AI",
    description: "Language and AI services for India — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><rect x="4" y="4" width="8" height="8" rx="2" stroke="currentColor" strokeWidth="1.3" /><path d="M8 1.5V4M8 12v2.5M1.5 8H4M12 8h2.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>,
  },
  "India commerce": {
    title: "India commerce",
    description: "Open commerce network in India — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M3 5h10l-1 8H4L3 5Zm2.5 0V4a2.5 2.5 0 0 1 5 0v1" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>,
  },
  "Smart home": {
    title: "Smart home",
    description: "Connected devices at home — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M2 8 8 2.5 14 8M4 7v6.5h8V7" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round" /></svg>,
  },
  Automation: {
    title: "Automation",
    description: "Workflow and trigger tools — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><path d="M8 2v3M8 11v3M2 8h3M11 8h3M4.2 4.2l2.1 2.1M9.7 9.7l2.1 2.1M11.8 4.2 9.7 6.3M6.3 9.7 4.2 11.8" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>,
  },
  "Integration protocol": {
    title: "Integration protocol",
    description: "Open agent-tool protocols — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><circle cx="4" cy="4" r="2" stroke="currentColor" strokeWidth="1.3" /><circle cx="12" cy="4" r="2" stroke="currentColor" strokeWidth="1.3" /><circle cx="8" cy="12" r="2" stroke="currentColor" strokeWidth="1.3" /><path d="M5.6 5.2 7 10.2M10.4 5.2 9 10.2M6 4h4" stroke="currentColor" strokeWidth="1.1" /></svg>,
  },
  "Agent runtime": {
    title: "Agent runtime",
    description: "Plugins that extend Daksh's own agent — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><rect x="3" y="3" width="10" height="10" rx="2" stroke="currentColor" strokeWidth="1.3" /><path d="M6 6h.01M10 6h.01M5.5 10c.7.7 1.6 1 2.5 1s1.8-.3 2.5-1" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>,
  },
  "Media": {
    title: "Media",
    description: "Music and speakers — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><circle cx="8" cy="8" r="5.5" stroke="currentColor" strokeWidth="1.3" /></svg>,
  },
  "Security": {
    title: "Security",
    description: "Password and secret managers — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><circle cx="8" cy="8" r="5.5" stroke="currentColor" strokeWidth="1.3" /></svg>,
  },
  "Utilities": {
    title: "Utilities",
    description: "Everyday information — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><circle cx="8" cy="8" r="5.5" stroke="currentColor" strokeWidth="1.3" /></svg>,
  },
  "AI models": {
    title: "AI models",
    description: "Model providers and gateways — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><circle cx="8" cy="8" r="5.5" stroke="currentColor" strokeWidth="1.3" /></svg>,
  },
  "Voice & search": {
    title: "Voice & search",
    description: "Speech, search and media generation — not built yet.",
    glyph: <svg viewBox="0 0 16 16" fill="none" className="h-4 w-4"><circle cx="8" cy="8" r="5.5" stroke="currentColor" strokeWidth="1.3" /></svg>,
  },
};
const CATEGORY_ORDER = [
  "Messaging",
  "Email",
  "Shopping",
  "Calendar",
  "Meetings",
  "Storage",
  "Documents",
  "Knowledge",
  "Data",
  "Social",
  "Developer",
  "Cloud",
  "Commerce",
  "Payments",
  "Accounting",
  "Maps",
  "Travel",
  "Mobility",
  "India travel",
  "India services",
  "India AI",
  "India commerce",
  "Smart home",
  "Media",
  "Security",
  "Utilities",
  "AI models",
  "Voice & search",
  "Automation",
  "Integration protocol",
  "Agent runtime",
];

const GOOGLE_WORKSPACE_KEYS = ["google-drive", "google-docs", "google-sheets", "google-slides"];
const DECORATIVE_PREVIEW = ["whatsapp", "gmail", "slack", "telegram", "microsoft-teams"];

function CategoryPills({ apps, active, onChange }: { apps: AppCatalogEntry[]; active: string; onChange: (c: string) => void }) {
  const counts = useMemo(() => {
    const byCategory = new Map<string, number>();
    for (const a of apps) byCategory.set(a.category, (byCategory.get(a.category) ?? 0) + 1);
    return byCategory;
  }, [apps]);

  const pills = [{ key: "All", label: "All Apps", glyph: null }, ...CATEGORY_ORDER.map((c) => ({ key: c, label: CATEGORY_META[c].title.split(" ")[0] === c ? c : c, glyph: CATEGORY_META[c].glyph })), { key: "More", label: "More", glyph: null }];

  return (
    <div className="flex flex-wrap gap-2">
      {pills.map((p) => (
        <button
          key={p.key}
          onClick={() => onChange(p.key)}
          className={`inline-flex items-center gap-1.5 rounded-full border px-4 py-2 text-sm font-medium transition-colors ${
            active === p.key ? "border-accent bg-accent text-accent-contrast" : "border-line bg-surface text-muted hover:bg-surface-2"
          }`}
        >
          {p.glyph}
          {p.label}
          {p.key !== "All" && p.key !== "More" && counts.get(p.key) ? <span className="opacity-70">({counts.get(p.key)})</span> : null}
        </button>
      ))}
    </div>
  );
}

function AppsPageContent({ apps }: { apps: AppCatalogEntry[] }) {
  const router = useRouter();
  const [category, setCategory] = useState("All");
  const [query, setQuery] = useState("");
  const [whatsAppOpen, setWhatsAppOpen] = useState(false);
  const [telegramOpen, setTelegramOpen] = useState(false);
  const [gmailOpen, setGmailOpen] = useState(false);
  const [microsoftOpen, setMicrosoftOpen] = useState(false);
  const [googleOpen, setGoogleOpen] = useState(false);
  const [slackOpen, setSlackOpen] = useState(false);
  const [oneAppOpen, setOneAppOpen] = useState<OneAppKey | null>(null);
  // The shopping apps: each is "connected" once the user turns it on. Its panel keeps their shopping list and opens items on the store.
  const [shoppingKey, setShoppingKey] = useState<string | null>(null);
  const { state: shopping, reload: reloadShopping } = useApi(() => api.shoppingStores());
  const shoppingStatus = (key: string): ConnectionStatusValue | undefined =>
    shopping.status !== "ready" ? undefined : shopping.data.find((s) => s.key === key)?.connected ? "Connected" : "NotConnected";
  const SHOPPING_KEYS = ["amazon", "flipkart"];
  // Microsoft sends the browser back here after sign-in (?microsoft=connected or ?error=...).
  const [microsoftNotice] = useState<string | null>(() => {
    if (typeof window === "undefined") return null;
    const p = new URLSearchParams(window.location.search);
    if (p.get("microsoft") === "connected") return "Microsoft connected.";
    return p.get("error") ? microsoftCallbackError(p.get("error")) : null;
  });
  // One Microsoft sign-in powers Outlook mail, Outlook calendar, OneDrive and Azure DevOps: each tile is "connected" when its service is on.
  const { state: microsoftDetail, reload: reloadMicrosoft } = useApi(() => api.microsoftStatus());
  // One Google sign-in powers Drive, Docs, Sheets and Slides (alongside Gmail and Calendar); Slack signs in on its own.
  const { state: googleDetail, reload: reloadGoogle } = useApi(() => api.googleStatus());
  const googleStatusFor = (service: "drive" | "docs" | "sheets" | "slides"): ConnectionStatusValue | undefined =>
    googleDetail.status !== "ready" ? undefined : googleDetail.data.services.find((x) => x.id === service)?.enabled ? "Connected" : "NotConnected";
  const { state: slackDetail, reload: reloadSlack } = useApi(() => api.slackStatus());
  const slackStatus: ConnectionStatusValue | undefined = slackDetail.status !== "ready" ? undefined : slackDetail.data.connected ? "Connected" : "NotConnected";
  // GitHub, Notion, Discord and Spotify each connect one account.
  const { state: githubDetail, reload: reloadGithub } = useApi(() => api.oneAppStatus("github"));
  const { state: notionDetail, reload: reloadNotion } = useApi(() => api.oneAppStatus("notion"));
  const { state: discordDetail, reload: reloadDiscord } = useApi(() => api.oneAppStatus("discord"));
  const { state: spotifyDetail, reload: reloadSpotify } = useApi(() => api.oneAppStatus("spotify"));
  const oneAppStatuses: Record<OneAppKey, ConnectionStatusValue | undefined> = {
    github: githubDetail.status === "ready" ? (githubDetail.data.connected ? "Connected" : "NotConnected") : undefined,
    notion: notionDetail.status === "ready" ? (notionDetail.data.connected ? "Connected" : "NotConnected") : undefined,
    discord: discordDetail.status === "ready" ? (discordDetail.data.connected ? "Connected" : "NotConnected") : undefined,
    spotify: spotifyDetail.status === "ready" ? (spotifyDetail.data.connected ? "Connected" : "NotConnected") : undefined,
  };
  const reloadOneApp = (key: OneAppKey) => ({ github: reloadGithub, notion: reloadNotion, discord: reloadDiscord, spotify: reloadSpotify })[key]();
  const microsoftStatusFor = (feature: "mail" | "calendar" | "onedrive" | "devops"): ConnectionStatusValue | undefined =>
    microsoftDetail.status !== "ready" ? undefined : microsoftDetail.data.connected && microsoftDetail.data.features.includes(feature) ? "Connected" : "NotConnected";
  // The catalogue list has no per-user connection data (it's not personalized) — fetch WhatsApp's real
  // status separately so its tile shows "Connected"/"Connecting"/etc. instead of always falling back to
  // the "Coming soon" badge every unbuilt integration gets.
  const { state: whatsAppDetail, reload: reloadWhatsAppDetail } = useApi(() => api.appDetail("whatsapp"));
  const whatsAppStatus = whatsAppDetail.status === "ready" ? (whatsAppDetail.data.connection?.status ?? "NotConnected") : undefined;
  const { state: gmailDetail, reload: reloadGmailDetail } = useApi(() => api.appDetail("gmail"));
  const gmailStatus: ConnectionStatusValue | undefined = gmailDetail.status === "ready"
    ? gmailDetail.data.connection?.status ?? "NotConnected"
    : undefined;

  // Google Calendar has no connection of its own: it is "connected" when any of your Google accounts was granted Calendar.
  const { state: calendarNow } = useApi(() => { const d = new Date(); const k = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`; return api.calendarEvents(k, k); });
  const calendarStatus: ConnectionStatusValue = calendarNow.status === "ready" && calendarNow.data.connected ? "Connected" : "NotConnected";

  const { state: telegramDetail, reload: reloadTelegramDetail } = useApi(() => api.appDetail("telegram"));
  const telegramStatus: ConnectionStatusValue | undefined = telegramDetail.status === "ready"
    ? telegramDetail.data.connection?.status ?? "NotConnected"
    : undefined;

  const statusOf = (key: string): ConnectionStatusValue | undefined =>
    key === "whatsapp" ? whatsAppStatus : key === "gmail" ? gmailStatus : key === "telegram" ? telegramStatus : key === "google-calendar" ? calendarStatus
      : key === "microsoft-outlook" ? microsoftStatusFor("mail") : key === "microsoft-outlook-calendar" ? microsoftStatusFor("calendar")
      : key === "onedrive" ? microsoftStatusFor("onedrive") : key === "azure-devops" ? microsoftStatusFor("devops")
      : key === "google-drive" ? googleStatusFor("drive") : key === "google-docs" ? googleStatusFor("docs") : key === "google-sheets" ? googleStatusFor("sheets") : key === "google-slides" ? googleStatusFor("slides")
      : key === "slack" ? slackStatus
      : key in ONE_APPS ? oneAppStatuses[key as OneAppKey]
      : SHOPPING_KEYS.includes(key) ? shoppingStatus(key) : undefined;
  const configureFor = (key: string): (() => void) | undefined =>
    key === "whatsapp" ? () => setWhatsAppOpen(true) : key === "telegram" ? () => setTelegramOpen(true) : key === "gmail" ? () => setGmailOpen(true) : key === "google-calendar" ? () => router.push("/calendar")
      : ["microsoft-outlook", "microsoft-outlook-calendar", "onedrive", "azure-devops"].includes(key) ? () => setMicrosoftOpen(true)
      : GOOGLE_WORKSPACE_KEYS.includes(key) ? () => setGoogleOpen(true) : key === "slack" ? () => setSlackOpen(true)
      : key in ONE_APPS ? () => setOneAppOpen(key as OneAppKey)
      : SHOPPING_KEYS.includes(key) ? () => setShoppingKey(key) : undefined;
  // Every app that works today (enabled), gathered at the top of the page: connected ones first, then the rest of the live apps.
  const showEnabledTop = category === "All" && !query;
  const enabledApps = apps.filter((a) => a.isEnabled)
    .sort((a, b) => Number(statusOf(b.integrationKey) === "Connected") - Number(statusOf(a.integrationKey) === "Connected"));
  const connectedCount = enabledApps.filter((a) => statusOf(a.integrationKey) === "Connected").length;

  const filtered = apps.filter((a) => {
    if (category !== "All" && category !== "More" && a.category !== category) return false;
    if (category === "More" && CATEGORY_ORDER.includes(a.category)) return false;
    if (query && !a.name.toLowerCase().includes(query.toLowerCase())) return false;
    return true;
  });

  // WhatsApp is the one live integration, so it always leads; these are pinned next by explicit request,
  // in this order; everything else keeps the catalogue's original order.
  const PINNED_ORDER = ["whatsapp", "telegram", "slack", "imessage", "facebook-messenger", "signal", "microsoft-teams"];
  const byPinnedFirst = (a: AppCatalogEntry, b: AppCatalogEntry) => {
    const ai = PINNED_ORDER.indexOf(a.integrationKey);
    const bi = PINNED_ORDER.indexOf(b.integrationKey);
    if (ai === -1 && bi === -1) return 0;
    if (ai === -1) return 1;
    if (bi === -1) return -1;
    return ai - bi;
  };

  const sections = CATEGORY_ORDER.filter((c) => category === "All" || category === c).map((c) => ({
    category: c,
    apps: filtered.filter((a) => a.category === c && !(showEnabledTop && a.isEnabled)).sort(byPinnedFirst),
  }));
  const showEmptyCategoryNotice = category !== "All" && category !== "More" && sections.every((s) => s.apps.length === 0) && !query;

  return (
    <>
      <header className={`mb-4 p-6 ${tileClass}`}>
        <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <p className="ai-mono text-xs font-bold uppercase tracking-[0.2em] text-accent">Integrations</p>
            <h1 className="mt-1 text-3xl font-extrabold">Apps &amp; Connections</h1>
            <p className="mt-1 max-w-xl text-sm text-muted">Connect your favourite apps with Daksh to chat, automate and get more done.</p>
          </div>
          <div className="hidden shrink-0 items-center -space-x-2 sm:flex">
            {DECORATIVE_PREVIEW.map((key) => {
              const app = apps.find((a) => a.integrationKey === key);
              if (!app) return null;
              return <BrandTile key={key} icon={app.icon} color={app.color} size={36} />;
            })}
          </div>
        </div>

        <div className="mt-5 flex flex-col gap-3 sm:flex-row">
          <div className="relative flex-1">
            <svg viewBox="0 0 16 16" fill="none" className="pointer-events-none absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-muted">
              <circle cx="7" cy="7" r="5" stroke="currentColor" strokeWidth="1.3" /><path d="m14 14-3-3" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
            </svg>
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search apps (e.g. WhatsApp, Gmail, Slack…)"
              className={`${inputClass} pl-10`}
            />
          </div>
        </div>

        <div className="mt-4">
          <CategoryPills apps={apps} active={category} onChange={setCategory} />
        </div>
      </header>

      {microsoftNotice && <p role="status" className="mb-4 rounded-[1.25rem] border border-line bg-surface p-4 text-sm font-semibold">{microsoftNotice}</p>}

      {showEnabledTop && enabledApps.length > 0 && (
        <section aria-label="Enabled apps" className="mb-6">
          <div className="mb-3 flex items-center justify-between px-1">
            <div>
              <h2 className="text-base font-bold">Enabled apps</h2>
              <p className="text-xs text-muted">Everything that works today, in one place.</p>
            </div>
            <span className="shrink-0 text-xs font-semibold text-muted">{connectedCount} of {enabledApps.length} connected</span>
          </div>
          <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            {enabledApps.map((app) => {
              const status = statusOf(app.integrationKey);
              const connected = status === "Connected";
              return (
                <li key={app.integrationKey}>
                  <button type="button" onClick={configureFor(app.integrationKey)} className={`flex w-full items-center gap-3 p-4 text-left transition-colors hover:bg-surface-2 ${tileClass}`}>
                    <BrandTile icon={app.icon} color={app.color} size={40} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-semibold">{app.name}</span>
                      <span className="mt-0.5 block">{status ? <StatusChip status={status} /> : <span className="text-xs text-muted">Checking…</span>}</span>
                    </span>
                    <span className="shrink-0 text-xs font-semibold text-accent">{connected ? "Manage" : "Connect"}</span>
                  </button>
                </li>
              );
            })}
          </ul>
        </section>
      )}

      {showEmptyCategoryNotice ? (
        <p className="rounded-[1.25rem] border border-dashed border-line bg-surface p-8 text-center text-sm text-muted">
          No apps in this category yet — check back soon.
        </p>
      ) : (
        sections.map(
          (s) =>
            s.apps.length > 0 && (
              <section key={s.category} className="mt-6 first:mt-0">
                <div className="mb-3 flex items-center justify-between px-1">
                  <div className="flex items-center gap-2.5">
                    <span className="flex h-8 w-8 items-center justify-center rounded-full bg-surface-2 text-text">{CATEGORY_META[s.category].glyph}</span>
                    <div>
                      <h2 className="text-base font-bold">{CATEGORY_META[s.category].title}</h2>
                      <p className="text-xs text-muted">{CATEGORY_META[s.category].description}</p>
                    </div>
                  </div>
                  <span className="shrink-0 text-xs font-semibold text-muted">{s.apps.length} app{s.apps.length === 1 ? "" : "s"}</span>
                </div>
                <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                  {s.apps.map((app) => (
                    <AppCard
                      key={app.integrationKey}
                      app={app}
                      priority={APP_META[app.integrationKey]?.priority ?? "Low"}
                      description={APP_META[app.integrationKey]?.description ?? app.description}
                      status={statusOf(app.integrationKey)}
                      onConfigure={configureFor(app.integrationKey)}
                    />
                  ))}
                </ul>
              </section>
            ),
        )
      )}

      <Modal
        wide
        open={whatsAppOpen}
        onClose={() => {
          setWhatsAppOpen(false);
          reloadWhatsAppDetail();
        }}
      >
        <WhatsAppConnectPanel />
      </Modal>

      <Modal
        wide
        open={telegramOpen}
        onClose={() => {
          setTelegramOpen(false);
          reloadTelegramDetail();
        }}
      >
        <TelegramConnectPanel />
      </Modal>

      <Modal wide open={shoppingKey !== null} onClose={() => { setShoppingKey(null); reloadShopping(); }}>
        {shoppingKey && (() => { const app = apps.find((a) => a.integrationKey === shoppingKey); return app ? <ShoppingPanel storeKey={shoppingKey} icon={app.icon} color={app.color} onChanged={reloadShopping} /> : null; })()}
      </Modal>

      <Modal wide fixedHeight open={googleOpen} onClose={() => { setGoogleOpen(false); reloadGoogle(); }}>
        <GoogleWorkspaceManagePanel onChanged={reloadGoogle} />
      </Modal>

      <Modal wide fixedHeight open={oneAppOpen !== null} onClose={() => { const k = oneAppOpen; setOneAppOpen(null); if (k) reloadOneApp(k); }}>
        {oneAppOpen && <OneAppManagePanel key={oneAppOpen} config={ONE_APPS[oneAppOpen]} onChanged={() => reloadOneApp(oneAppOpen)} />}
      </Modal>

      <Modal wide fixedHeight open={slackOpen} onClose={() => { setSlackOpen(false); reloadSlack(); }}>
        <SlackManagePanel onChanged={reloadSlack} />
      </Modal>

      <Modal wide fixedHeight open={microsoftOpen} onClose={() => { setMicrosoftOpen(false); reloadMicrosoft(); }}>
        <MicrosoftManagePanel onChanged={reloadMicrosoft} />
      </Modal>

      <Modal
        wide
        fixedHeight
        open={gmailOpen}
        onClose={() => {
          setGmailOpen(false);
          reloadGmailDetail();
        }}
      >
        <GmailManagePanel onChanged={reloadGmailDetail} />
      </Modal>
    </>
  );
}

export default function AppsClient() {
  const { state, reload } = useApi(() => api.appCatalog());
  return (
    <main className="w-full p-3 md:p-4">
      {state.status === "loading" && <Loading label="Loading apps" />}
      {state.status === "error" && <ErrorState error={state.error} onRetry={reload} />}
      {state.status === "ready" && <AppsPageContent apps={state.data} />}
    </main>
  );
}
