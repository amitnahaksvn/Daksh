# Amit.md — keys, secrets and setup for the 20 new apps

Everything in Daksha for these 20 apps is built and tested with mock servers. **None of it has been run against the real services**, because that needs
your developer accounts and keys. Until a key below is set, the app shows "not set up on the server yet" and nothing breaks. Add the keys, restart the
API, open **Apps**, and use the Manage popup.

How the apps work (same as Gmail / Telegram / Spotify):

- **Apps page** → click the app → **Manage** popup: **General** (account, full-access switch, "show on dashboard", Disconnect), **Dashboard** (what the tile
  shows and how many items) and, where the app has them, **Preferences**. The page `/apps/<app>` has a tab per list plus a **Do something** tab.
- **Read-only by default.** Writing (post, create, send, switch on) needs the full-access switch. Where the provider has a separate permission, turning the
  switch on asks the person to approve it in a popup. Amazon Selling Partner, DigiLocker, IRCTC, Booking.com and Google Maps are read-only by design.
- Credentials are stored encrypted (same envelope as the other apps). Disconnect deletes them.

Where to put the values: the API's environment (or `.env`, see `.env.example`). Section names use `__` in env vars (`Shopify__OAuth__ClientId` is
`Shopify:OAuth:ClientId` in `appsettings`). For OAuth apps the **redirect URI** you register with the provider must be exactly
`<API address>/api/integrations/<app-key>/callback`, for example `http://localhost:5080/api/integrations/x/callback`. Several providers refuse
`http://localhost`; use your real HTTPS address (for example `https://api.daksha.example.com/...`) and set `<Section>__OAuth__RedirectUri` and
`<Section>__OAuth__FrontendUrl` (the page the person returns to, `https://app.../apps/<app-key>`) to match.

## At a glance

| App | Key | Sign-in | Server settings to add | Needs approval from the provider? |
|---|---|---|---|---|
| YouTube | `youtube` | Google | reuses `Gmail__OAuth__*` (or `YouTube__OAuth__*`) | No (sensitive scope warning until verified) |
| Google Maps Platform | `google-maps-platform` | none (server key) | `GoogleMaps__ApiKey` | No |
| Uber | `uber` | OAuth | `Uber__OAuth__ClientId/ClientSecret` | **Yes** for ride requests |
| Yahoo Mail | `yahoo-mail` | person's app password | none | No |
| Proton Mail | `proton-mail` | person's Bridge password | none | No (paid Proton plan + Bridge) |
| Apple Calendar | `apple-calendar` | person's app-specific password | none | No |
| Instagram | `instagram-social` | OAuth | `Instagram__OAuth__ClientId/ClientSecret` | **Yes** (Meta app review) |
| Facebook Pages | `facebook-pages` | OAuth | `Facebook__OAuth__ClientId/ClientSecret` | **Yes** (Meta app review) |
| X | `x` | OAuth 2.0 PKCE | `X__OAuth__ClientId/ClientSecret` | Paid plan for most reads |
| LinkedIn | `linkedin` | OAuth | `LinkedIn__OAuth__ClientId/ClientSecret` | Products must be added |
| TikTok | `tiktok` | OAuth | `TikTok__OAuth__ClientId/ClientSecret` | **Yes** (app review) |
| Reddit | `reddit` | OAuth | `Reddit__OAuth__ClientId/ClientSecret` | No |
| Pinterest | `pinterest` | OAuth | `Pinterest__OAuth__ClientId/ClientSecret` | Trial access first |
| Amazon Selling Partner API | `amazon-selling-partner` | OAuth (Seller Central) | `AmazonSellingPartner__*` | **Yes** (developer registration) |
| Shopify | `shopify` | OAuth per store | `Shopify__OAuth__ClientId/ClientSecret` | No (custom/partner app) |
| Booking.com | `booking-com` | person's partner token | none | **Yes** (Demand API partner) |
| QuickBooks Online | `quickbooks-online` | OAuth | `QuickBooks__OAuth__ClientId/ClientSecret` | Production keys need Intuit review |
| Home Assistant | `home-assistant` | person's URL + token | none | No |
| IRCTC | `irctc` | person's RapidAPI key | none | No (RapidAPI subscription) |
| DigiLocker | `digilocker` | OAuth | `DigiLocker__OAuth__ClientId/ClientSecret` | **Yes** (government partner registration) |

---

## YouTube
**Server settings:** nothing new if Gmail already works. Daksha reuses `Gmail__OAuth__ClientId` and `Gmail__OAuth__ClientSecret`. To use a separate Google client, set
`YouTube__OAuth__ClientId` and `YouTube__OAuth__ClientSecret`.
1. Google Cloud Console → your project → **APIs & Services → Library** → enable **YouTube Data API v3**.
2. **OAuth consent screen** → add scopes `.../auth/youtube.readonly` and `.../auth/youtube`.
3. **Credentials** → your OAuth client → add the redirect URI `…/api/integrations/youtube/callback`.
4. Optional: `YouTube__OAuth__RedirectUri`, `YouTube__OAuth__FrontendUrl`.
Notes: the YouTube API has a daily quota (10,000 units; a search costs 100). Full access lets Daksha create playlists, add videos and like videos. "Watch later" is not exposed by YouTube's API, so it is not listed.

## Google Maps Platform
**Server settings:** `GoogleMaps__ApiKey`
1. Google Cloud Console → enable **Places API (New)** and **Routes API**, and turn billing on.
2. **Credentials → Create credentials → API key.** Restrict it to those two APIs and to your server's IP address.
Notes: nothing to sign in to; any person can "Turn on Maps". In **Manage → Preferences** each person sets a home and work address, which powers the commute tile ("leave now") and the assistant's travel-time answers.

## Uber
**Server settings:** `Uber__OAuth__ClientId`, `Uber__OAuth__ClientSecret` (plus optional `Uber__OAuth__RedirectUri`, `Uber__OAuth__FrontendUrl`)
1. developer.uber.com → **Dashboard → Create app** (Rides API). Copy the Client ID and Client Secret.
2. Add the redirect URI `…/api/integrations/uber/callback`.
3. Scopes: `profile history` are available to new apps; **`request` (booking rides) needs Uber's approval**. Until then full access will be refused by Uber.
Notes: Uber has reduced access to its Rides API; if your account cannot create an app, the history and estimate lists will not work. Price estimates take two "latitude, longitude" pairs.

## Yahoo Mail
**Server settings:** none. Each person connects their own mailbox.
1. Yahoo → **Account Info → Account Security → Generate app password** (turn on two-step verification first).
2. In Daksha paste the Yahoo address and that app password. Daksha uses `imap.mail.yahoo.com:993` and `smtp.mail.yahoo.com:465`.
Notes: reads the inbox read-only; sending needs the full-access switch.

## Proton Mail
**Server settings:** none.
1. Proton has no public API. Install **Proton Mail Bridge** (needs a paid Proton plan) on a machine the Daksha server can reach.
2. In Bridge copy the address, the **Bridge password** (not your Proton password), and the IMAP/SMTP ports (defaults `1143` / `1025`).
3. In Daksha paste them. If Daksha runs in the cloud and Bridge runs on your PC, you must expose Bridge through a private tunnel (VPN/Tailscale); Daksha refuses link-local addresses but allows LAN and loopback addresses.
Notes: Bridge uses a self-signed certificate, so the certificate is only skipped when the address is loopback or on a private network.

## Apple Calendar
**Server settings:** none.
1. appleid.apple.com → **Sign-In and Security → App-Specific Passwords** → generate one.
2. In Daksha enter the Apple ID and that password. Daksha talks CalDAV to `caldav.icloud.com` only.
Notes: events show in UTC; Preferences has "My time zone" (for example `Asia/Kolkata`) used when Daksha adds an event.

## Instagram
**Server settings:** `Instagram__OAuth__ClientId` (Instagram app ID), `Instagram__OAuth__ClientSecret` (Instagram app secret), `Instagram__OAuth__RedirectUri`, `Instagram__OAuth__FrontendUrl`
1. developers.facebook.com → **Create app** (type Business) → add the product **Instagram** → **API setup with Instagram login**.
2. Copy **Instagram app ID** and **Instagram app secret**. Add the redirect URI `…/api/integrations/instagram-social/callback` (Meta requires **HTTPS**).
3. Permissions used: `instagram_business_basic`, `instagram_business_manage_insights` (read) and `instagram_business_content_publish`, `instagram_business_manage_comments`, `instagram_business_manage_messages` (full access).
4. While the app is in development only accounts added as **Instagram testers** can sign in; for everyone else submit **App Review**.
Notes: only Instagram **business or creator** accounts work. Publishing needs a public JPEG link. Replies to DMs are allowed within 24 hours of the person's last message.

## Facebook Pages
**Server settings:** `Facebook__OAuth__ClientId` (App ID), `Facebook__OAuth__ClientSecret`, `Facebook__OAuth__RedirectUri`, `Facebook__OAuth__FrontendUrl`
1. developers.facebook.com → **Create app** (Business) → add **Facebook Login for Business** (or Facebook Login).
2. Add the redirect URI `…/api/integrations/facebook-pages/callback` (HTTPS).
3. Permissions: `pages_show_list`, `pages_read_engagement`, `pages_read_user_content`, `read_insights` (read); `pages_manage_posts`, `pages_manage_engagement`, `pages_messaging` (full access). Submit them for **App Review** and complete **Business Verification** to use them for anyone but app roles.
Notes: in **Manage → Preferences → Which Page** each person can choose the Page by name; the first Page is used otherwise.

## X (Twitter)
**Server settings:** `X__OAuth__ClientId`, `X__OAuth__ClientSecret`, `X__OAuth__RedirectUri`, `X__OAuth__FrontendUrl`
1. developer.x.com → your project/app → **User authentication settings** → enable **OAuth 2.0**, type **Web App (confidential client)**, set the callback URL `…/api/integrations/x/callback`.
2. Copy the OAuth 2.0 **Client ID** and **Client Secret** (not the API key/secret).
3. Scopes used: `tweet.read users.read offline.access bookmark.read` (read), `tweet.write like.write` (full access).
Notes: X's plans limit what works: the free tier allows only posting; timeline, mentions and search need a paid plan. Daksha shows X's own error when a call is not on the plan.

## LinkedIn
**Server settings:** `LinkedIn__OAuth__ClientId`, `LinkedIn__OAuth__ClientSecret` (optional `LinkedIn__ApiVersion`, default `202510`)
1. linkedin.com/developers → **Create app** (needs a LinkedIn Page).
2. **Products** tab → add **Sign In with LinkedIn using OpenID Connect** and **Share on LinkedIn**.
3. **Auth** tab → add the redirect URL `…/api/integrations/linkedin/callback`.
Notes: LinkedIn does not let ordinary apps read the feed or messages, so Daksha shows the profile and can publish posts (full access). The "My posts" list works only for approved apps and says so otherwise. LinkedIn access tokens last 60 days; after that the person reconnects.

## TikTok
**Server settings:** `TikTok__OAuth__ClientId` (the **client key**), `TikTok__OAuth__ClientSecret`, `TikTok__OAuth__RedirectUri`, `TikTok__OAuth__FrontendUrl`
1. developers.tiktok.com → **Manage apps → Connect an app**. Add products **Login Kit** and **Content Posting API**.
2. Add the redirect URI `…/api/integrations/tiktok/callback` (HTTPS). Scopes: `user.info.basic user.info.stats video.list`, and `video.upload` for full access.
3. Submit the app for review. Sending a video to drafts by link also needs your video domain verified in the TikTok developer portal.
Notes: until the app is approved only the developer's own account (a "target user") can sign in.

## Reddit
**Server settings:** `Reddit__OAuth__ClientId`, `Reddit__OAuth__ClientSecret` (optional `Reddit__UserAgent`, for example `web:daksha:1.0 (by /u/yourname)`)
1. reddit.com/prefs/apps → **create another app** → type **web app**, redirect uri `…/api/integrations/reddit/callback`.
2. The Client ID is the string under the app name; the secret is "secret".
Notes: Reddit requires a descriptive User-Agent; set `Reddit__UserAgent` with your Reddit username. Full access adds `submit edit vote privatemessages` (also needed to read the inbox).

## Pinterest
**Server settings:** `Pinterest__OAuth__ClientId` (App ID), `Pinterest__OAuth__ClientSecret`; for trial access also `Pinterest__ApiBase=https://api-sandbox.pinterest.com/v5`
1. developers.pinterest.com → **My apps → Connect app**. Redirect URI `…/api/integrations/pinterest/callback`.
2. New apps start with **trial access** (sandbox: data is separate from the real account). Apply for **standard access** to use real data, then remove `Pinterest__ApiBase`.
Notes: scopes `user_accounts:read boards:read pins:read` (read), `boards:write pins:write` (full access).

## Amazon Selling Partner API
**Server settings:** `AmazonSellingPartner__OAuth__ClientId`, `AmazonSellingPartner__OAuth__ClientSecret` (Login with Amazon credentials), `AmazonSellingPartner__ApplicationId` (`amzn1.sp.solution....`), and optionally
`AmazonSellingPartner__Region` (`eu` default, `na` or `fe`), `AmazonSellingPartner__MarketplaceId` (default `A21TJRUUN4KXV` = Amazon.in), `AmazonSellingPartner__SellerCentralUrl` (default `https://sellercentral.amazon.in`), `AmazonSellingPartner__Draft=false` once the app is published.
1. Register as an SP-API developer in Seller Central (Apps and Services → Develop Apps) and create the app. Choose the roles Orders, Listings and Inventory.
2. Create **Login with Amazon** security profile credentials and set the redirect URI `…/api/integrations/amazon-selling-partner/callback`.
3. While the app is a **draft**, only your own seller account can authorize it (Daksha adds `version=beta`).
Notes: read-only (orders from the last 30 days, listings, FBA inventory). The seller then clicks "Continue to Seller Central" and authorizes.

## Shopify
**Server settings:** `Shopify__OAuth__ClientId` (API key), `Shopify__OAuth__ClientSecret`, `Shopify__OAuth__RedirectUri`, `Shopify__OAuth__FrontendUrl`
1. partners.shopify.com → **Apps → Create app → Create app manually** (a "custom distribution" app works for your own stores).
2. **App setup → Allowed redirection URL(s)** → `…/api/integrations/shopify/callback`.
3. Each person types their shop address (`my-store.myshopify.com`) on connect. Scopes: `read_orders read_products read_customers` (read), `write_products write_orders` (full access).
Notes: Daksha verifies Shopify's signature (HMAC) on the callback. New products are created as **drafts**. API version `2025-01`.

## Booking.com
**Server settings:** none. Each person enters partner credentials.
1. Booking.com **Demand API** is only for approved partners (apply through the Booking.com Affiliate/Connectivity partner programme).
2. Paste the **Demand API token** and your **Affiliate ID** in Daksha.
Notes: search by numeric city id (use "Find a city id") and dates. Written against Demand API 3.1; if your account is on a different version the response mapping may need a tweak.

## QuickBooks Online
**Server settings:** `QuickBooks__OAuth__ClientId`, `QuickBooks__OAuth__ClientSecret`, `QuickBooks__Environment` (`sandbox` or `production`), `QuickBooks__OAuth__RedirectUri`, `QuickBooks__OAuth__FrontendUrl`
1. developer.intuit.com → **Dashboard → Create an app** (QuickBooks Online and Payments), scope **Accounting**.
2. **Keys & credentials** → use the Development keys first with `QuickBooks__Environment=sandbox`; add the redirect URI `…/api/integrations/quickbooks-online/callback`.
3. For real company data switch to the **Production** keys (needs Intuit's app assessment) and `QuickBooks__Environment=production`.
Notes: QuickBooks has one permission for reading and changing books, so full access is Daksha's own switch. Creating invoices uses item id `1` ("Services"); create it first if your books lack it.

## Home Assistant
**Server settings:** none.
1. Home Assistant → your **Profile → Security → Long-lived access tokens → Create token**.
2. In Daksha enter the address (`http://homeassistant.local:8123`) and the token.
3. The Daksha **server** must be able to reach that address. For a cloud server use Nabu Casa's remote URL or a private tunnel. Daksha refuses link-local / cloud-metadata addresses.
Notes: full access lets Daksha switch devices, set brightness and activate scenes, scripts and automations.

## IRCTC
**Server settings:** none. Each person enters a RapidAPI key.
1. IRCTC has **no public API**, so Daksha uses the third-party "IRCTC" service on rapidapi.com (host `irctc1.p.rapidapi.com`). Subscribe to it and copy your **X-RapidAPI-Key**.
2. Paste the key in Daksha. In **Manage → Preferences → My PNR numbers** add up to 5 PNRs to show on the dashboard.
Notes: PNR status, trains between stations, live running status, station-code search. **Booking tickets is not possible through this**; do that on irctc.co.in.

## DigiLocker
**Server settings:** `DigiLocker__OAuth__ClientId`, `DigiLocker__OAuth__ClientSecret`, `DigiLocker__OAuth__RedirectUri`, `DigiLocker__OAuth__FrontendUrl`, optional `DigiLocker__BaseUrl` (default `https://digilocker.meripehchaan.gov.in`)
1. Register as a DigiLocker **requester/partner** (API Setu → DigiLocker, or the DigiLocker partner portal) and request OAuth credentials. Register the redirect URI `…/api/integrations/digilocker/callback`.
2. Government onboarding takes time; until then the app shows "not set up".
Notes: read-only. Daksha lists the documents issued to the person (name, issuer, date) and never downloads them.

---

## Checking each app after you add keys (a 2-minute smoke test)

1. Restart the API. Open **Apps** → the app → it should no longer say "not set up".
2. Connect. For OAuth apps a popup opens, you approve, and the popup closes by itself.
3. Open `/apps/<app-key>` → each tab should list something; **Manage → General** shows the account.
4. Turn on **Show on dashboard** and confirm the tile appears.
5. In **Ask**, try a question that names the app ("what's on my YouTube playlists?").
6. Turn on full access and try one action (a draft/test item is safest).

If something fails, the message shown is the provider's own reason where available (for example a plan limit or a missing permission). The API logs
carry the same detail without secrets.

---

## More apps (generated)

The apps below were added in later batches. This section is written from the app definitions (`ProviderDocs`), so it always matches the code.
Where you see `<API address>`, use the address of your API (for example `https://api.example.com`).

<!-- BEGIN GENERATED APPS -->
### 1Password

App key `1password`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: Connect server address, Connect token.

1. Nothing to set on the server. Each person runs a **1Password Connect server** (1password.com → Developer → Secrets Automation) and creates a token with read access to the vaults to share.
2. In Daksha they enter the Connect server address and the token.

Notes: By design Daksha asks only for the item list, which contains titles, categories and dates, never field values.

### Airtable

App key `airtable`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Personal access token.

1. Nothing to set on the server. Each person creates a **personal access token** at airtable.com/create/tokens with `data.records:read` and `schema.bases:read` (add `data.records:write` for adding records) and chooses the bases to share.

### Amadeus

App key `amadeus`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: API key, API secret, Environment.

1. Nothing to set on the server. Each person registers at developers.amadeus.com, creates an app (Self-Service) and enters its **API key** (client id) and **secret**. Use Test to start; switch to Production once approved.

Notes: The Test environment returns cached sample data.

### Amazon Bedrock

App key `amazon-bedrock`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Access key id, Secret access key, Region.

1. Nothing to set on the server. Each person creates an **IAM user** with `AmazonBedrockReadOnly` (add `bedrock:InvokeModel` to send prompts), an access key for it, and enables model access in the Bedrock console.

Notes: Requests are signed with AWS Signature Version 4.

### Anthropic

App key `anthropic`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at console.anthropic.com/settings/keys.

### Anthropic on Vertex

App key `anthropic-vertex`. Sign-in: OAuth. Read-only until full access is turned on.

**Server settings:** `Vertex__OAuth__ClientId`, `Vertex__OAuth__ClientSecret` (or reuse `GoogleCloud__OAuth__ClientId` / `ClientSecret`); optional `Vertex__OAuth__RedirectUri` and `Vertex__OAuth__FrontendUrl`.

1. Reuses the Google client (`Gmail__OAuth__*`). Add the scope `.../auth/cloud-platform` to the consent screen, enable the **Vertex AI API** and enable the Claude models in Vertex AI Model Garden for your project.
2. After connecting, set the project and region in Manage → Preferences.
3. Register the redirect URI `<API address>/api/integrations/anthropic-vertex/callback` with the provider (many providers require HTTPS).

Notes: Vertex has no list of Anthropic models, so you type the model id when sending a prompt.

### AWS

App key `aws`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: Access key id, Secret access key, Region.

1. Nothing to set on the server. Each person creates an **IAM user** with the managed policy `ReadOnlyAccess` (IAM → Users → Create user), then creates an **access key** for it.
2. In Daksha they enter the access key id, the secret access key and a default region (for example `ap-south-1`).

Notes: Requests are signed with AWS Signature Version 4. Use a dedicated read-only user, never your root account.

### Azure Speech

App key `azure-speech`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: Speech key, Region.

1. Nothing to set on the server. In the Azure portal create a **Speech** resource and copy one of its **keys** and its **region** (for example `centralindia`).

### Bhashini

App key `bhashini`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: User ID, ULCA API key.

1. Nothing to set on the server. Each person registers at bhashini.gov.in, opens **My Profile → API keys** and copies their **User ID** and **ULCA API key**.

Notes: Uses the Bhashini pipeline service (look up the model, then run it). Languages: English, Hindi, Bengali, Tamil, Telugu, Marathi, Gujarati, Kannada, Malayalam, Punjabi, Odia, Assamese, Urdu, Sanskrit, Nepali.

### Bitbucket Cloud

App key `bitbucket`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Bitbucket username, App password.

1. Nothing to set on the server. Each person creates an **app password** (Bitbucket → Personal settings → App passwords) with Account: read, Repositories: read and Pull requests: read (add write to comment).
2. In Daksha they enter their Bitbucket username and the app password.

### Bluesky

App key `bluesky`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Handle, App password, Server.

1. Nothing to set on the server. Each person creates an **app password** in Bluesky (Settings → Privacy and security → App passwords) and enters it with their handle (for example `you.bsky.social`).

### Box

App key `box`. Sign-in: OAuth. Read-only until full access is turned on.

**Server settings:** `Box__OAuth__ClientId`, `Box__OAuth__ClientSecret`; optional `Box__OAuth__RedirectUri` and `Box__OAuth__FrontendUrl`.

1. Box Developer Console → **Create New App** → Custom App → User Authentication (OAuth 2.0). Copy the client id and secret.
2. Configuration tab: set the scopes to read and write all files and folders (Daksha only writes when full access is on).
3. Register the redirect URI `<API address>/api/integrations/box/callback` with the provider (many providers require HTTPS).

### Calendly

App key `calendly`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Personal access token.

1. Nothing to set on the server. Each person creates a **personal access token** in Calendly (Integrations → API & Webhooks) and pastes it into Daksha.

### Cerebras

App key `cerebras`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at cloud.cerebras.ai.

### Cloudflare

App key `cloudflare`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API token.

1. Nothing to set on the server. Each person creates an **API token** (My Profile → API Tokens) scoped to Zone:Read and DNS:Read (plus DNS:Edit and Cache Purge for full access).

### Cloudflare AI Gateway

App key `cloudflare-ai-gateway`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Cloudflare account id, Gateway name, API key, Gateway token (if authenticated).

1. Nothing to set on the server. In Cloudflare create an **AI Gateway** (AI → AI Gateway) and note the account id and gateway name.
2. Enter them with the OpenAI-compatible provider key, and a gateway token if the gateway is authenticated.

### Cohere

App key `cohere`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at dashboard.cohere.com/api-keys.

### ComfyUI

App key `comfyui`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: ComfyUI address.

1. Nothing to set on the server. Run ComfyUI where the Daksha server can reach it (default `http://127.0.0.1:8188`; use a private tunnel for a cloud server).

### Confluence

App key `confluence`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Confluence site, Atlassian email, API token.

1. Nothing to set on the server. Each person creates an **API token** at id.atlassian.com → Security → API tokens and enters it with their site and email.

Notes: Confluence Cloud only (not Data Center).

### Custom compatible API

App key `custom-openai-api`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Server address, API key (if the server needs one).

1. Nothing to set on the server. Each person enters the **base address** of an OpenAI-compatible service (for example `https://api.example.com/v1`) and its key.

### Deepgram

App key `deepgram`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** in the Deepgram console (API Keys) with the Member role.

### DeepSeek

App key `deepseek`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at platform.deepseek.com/api_keys.

### Docker Hub

App key `docker-hub`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: Docker Hub username, Access token.

1. Nothing to set on the server. Each person creates a **personal access token** with Read-only permission (Docker Hub → Account settings → Personal access tokens).
2. In Daksha they enter their Docker Hub username and the token.

### Dropbox

App key `dropbox`. Sign-in: OAuth. Read-only until full access is turned on.

**Server settings:** `Dropbox__OAuth__ClientId`; optional `Dropbox__OAuth__RedirectUri` and `Dropbox__OAuth__FrontendUrl`.

1. Dropbox App Console → **Create app** → Scoped access → Full Dropbox. Copy the app key (client id) and app secret.
2. Permissions tab: enable `account_info.read`, `files.metadata.read` and `files.content.write`, then Submit.
3. Register the redirect URI `<API address>/api/integrations/dropbox/callback` with the provider (many providers require HTTPS).

### eBay

App key `ebay`. Sign-in: OAuth. Read-only.

**Server settings:** `EBay__OAuth__ClientId`, `EBay__OAuth__ClientSecret`; optional `EBay__OAuth__RedirectUri` and `EBay__OAuth__FrontendUrl`.

1. developer.ebay.com → Hardware/Application keys: create a keyset (use Sandbox first). Copy the **App ID** (client id) and **Cert ID** (client secret).
2. User Tokens → **Get a Token from eBay via Your Application**: create a **RuName** whose “Auth accepted URL” is `<API address>/api/integrations/ebay/callback`. Set it as `EBay__RuName`.
3. Set `EBay__Environment=sandbox` while testing, and remove it for production.
4. Register the redirect URI `<API address>/api/integrations/ebay/callback` with the provider (many providers require HTTPS).

Notes: eBay's redirect value is the RuName, not a URL. Production keys need eBay's application growth check for some APIs.

### ElevenLabs

App key `elevenlabs`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at elevenlabs.io (Profile → API keys).

### fal

App key `fal`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at fal.ai/dashboard/keys.

Notes: fal cannot check a key without spending credits, so the key is only checked the first time an image is generated.

### Firebase

App key `firebase`. Sign-in: OAuth. Read-only.

**Server settings:** `Firebase__OAuth__ClientId`, `Firebase__OAuth__ClientSecret` (or reuse `Gmail__OAuth__ClientId` / `ClientSecret`); optional `Firebase__OAuth__RedirectUri` and `Firebase__OAuth__FrontendUrl`.

1. Reuses the Google client (`Gmail__OAuth__*`). In Google Cloud Console enable the **Firebase Management API** and add the scope `.../auth/firebase.readonly` to the consent screen.
2. Register the redirect URI `<API address>/api/integrations/firebase/callback` with the provider (many providers require HTTPS).

### Fireworks

App key `fireworks`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at fireworks.ai/account/api-keys.

### GitHub Copilot

App key `github-copilot`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates a **fine-grained personal access token** at github.com/settings/tokens with the `models:read` permission.

Notes: This uses GitHub Models (models.github.ai). GitHub does not offer a public API to the Copilot chat service itself.

### GitLab

App key `gitlab`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: GitLab address, Personal access token.

1. Nothing to set on the server. Each person creates a **personal access token** in GitLab (Preferences → Access tokens) with the `read_api` scope (add `api` to allow commenting and creating issues).
2. In Daksha they enter their GitLab address (`https://gitlab.com` or their own server) and the token.

Notes: Self-hosted servers must be reachable from the Daksha server.

### Google Cloud

App key `google-cloud`. Sign-in: OAuth. Read-only.

**Server settings:** `GoogleCloud__OAuth__ClientId`, `GoogleCloud__OAuth__ClientSecret` (or reuse `Gmail__OAuth__ClientId` / `ClientSecret`); optional `GoogleCloud__OAuth__RedirectUri` and `GoogleCloud__OAuth__FrontendUrl`.

1. Reuses the Google client (`Gmail__OAuth__*`). In Google Cloud Console enable the **Cloud Resource Manager**, **Compute Engine** and **Cloud Run Admin** APIs and add the scope `.../auth/cloud-platform.read-only` to the consent screen.
2. Register the redirect URI `<API address>/api/integrations/google-cloud/callback` with the provider (many providers require HTTPS).

### Google Gemini

App key `google-gemini`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at aistudio.google.com/apikey.

### Groq

App key `groq`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at console.groq.com/keys.

### Hugging Face

App key `hugging-face`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates a **fine-grained token** at huggingface.co/settings/tokens with “Make calls to Inference Providers”.

### IFTTT

App key `ifttt`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Webhooks key.

1. Nothing to set on the server. Each person turns on the **Webhooks** service in IFTTT (ifttt.com/maker_webhooks) and copies their key from Documentation.

Notes: IFTTT cannot be read back from; Daksha only fires events. Connecting fires one harmless test event named `daksha_connected`.

### Jira

App key `jira`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Jira site, Atlassian email, API token.

1. Nothing to set on the server. Each person creates an **API token** at id.atlassian.com → Security → API tokens.
2. In Daksha they enter their site (`your-team.atlassian.net`), Atlassian email and the token.

Notes: Jira Server / Data Center is not covered (Jira Cloud only).

### Kubernetes

App key `kubernetes`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API server address, Service-account token, Cluster CA certificate (PEM).

1. Nothing to set on the server. Create a **service account** with the built-in `view` ClusterRole, then create a token for it: `kubectl create token <name> --duration=8760h`.
2. In Daksha enter the API server address (`https://…:6443`), the token and, if the cluster uses a private certificate authority, its certificate (PEM).
3. The Daksha server must be able to reach the API server.

Notes: A private CA is trusted only for that cluster's connection. Public cloud clusters with a public certificate need no CA.

### LiteLLM

App key `litellm`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Server address, API key.

1. Nothing to set on the server. Each person enters the address of their own **LiteLLM proxy** and a virtual key. The Daksha server must be able to reach the proxy.

### LM Studio

App key `lm-studio`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Server address, API key (if the server needs one).

1. Nothing to set on the server. In LM Studio start the **local server** (default `http://localhost:1234`). The Daksha server must be able to reach it.

### Make

App key `make`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Name, Webhook address.

1. Nothing to set on the server. In Make create a scenario that starts with **Webhooks → Custom webhook** and copy its address (`https://hook.eu1.make.com/…`).
2. In Daksha enter a name and that address. Add more scenarios later in Manage → Preferences.

Notes: Only addresses on hook.eu1.make.com, hook.us1.make.com, … are accepted. Daksha cannot read results back from Make; it only starts the trigger.

### Mapbox

App key `mapbox`. One server key; nothing to sign in to. Read-only.

**Server settings:** `Mapbox__AccessToken`.

1. account.mapbox.com → Tokens → **Create a token** with the default public scopes. Set it as `Mapbox__AccessToken`. Restrict it to your server's URL if you can.

Notes: Mapbox only accepts the token in the address of each request, so avoid logging full request URLs.

### Mastodon

App key `mastodon`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Instance, Access token.

1. Nothing to set on the server. Each person opens their instance → Preferences → **Development → New application**, grants `read` (and `write:statuses` for posting), and copies **your access token**.
2. In Daksha they enter the instance address and the token.

### MCP servers

App key `mcp-servers`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: MCP server address, Bearer token.

1. Nothing to set on the server. Each person enters the **address of an MCP server** (streamable HTTP, for example `https://mcp.example.com/mcp`) and, if required, a bearer token.
2. Listing tools is read-only; calling a tool needs the full-access switch.

Notes: Only the streamable-HTTP transport is supported (not stdio). Each call opens a short-lived session.

### Microsoft Azure

App key `microsoft-azure`. Sign-in: OAuth. Read-only.

**Server settings:** `AzureCloud__OAuth__ClientId`, `AzureCloud__OAuth__ClientSecret` (or reuse `Microsoft__OAuth__ClientId` / `ClientSecret`); optional `AzureCloud__OAuth__RedirectUri` and `AzureCloud__OAuth__FrontendUrl`.

1. Reuses the Microsoft app registration (`Microsoft__OAuth__ClientId` / `ClientSecret`), or set `AzureCloud__OAuth__*` for a separate one.
2. In Entra ID → App registrations → your app → API permissions → add **Azure Service Management → user_impersonation** (delegated).
3. Register the redirect URI `<API address>/api/integrations/microsoft-azure/callback` with the provider (many providers require HTTPS).

Notes: Daksha only calls read endpoints. The Azure permission itself is broad (user_impersonation), so use a dedicated app registration if that matters to you.

### MiniMax

App key `minimax`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at platform.minimax.io.

Notes: MiniMax does not always list its models; type the model name when sending a prompt.

### Mistral

App key `mistral`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at console.mistral.ai/api-keys.

### Moonshot / Kimi

App key `moonshot-kimi`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at platform.moonshot.ai.

### n8n

App key `n8n`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: n8n address, API key.

1. Nothing to set on the server. Each person creates an **API key** in their n8n (Settings → n8n API) and enters it with the address of their n8n.
2. The Daksha server must be able to reach that address.

### NVIDIA

App key `nvidia`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person generates an **API key** at build.nvidia.com.

### Obsidian

App key `obsidian`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Obsidian address, API key.

1. Nothing to set on the server. In Obsidian install the **Local REST API** plugin, enable it and copy its API key.
2. Enter its address (`https://127.0.0.1:27124`, or `http://127.0.0.1:27123` with the non-encrypted server enabled) and the key. The Daksha server must be able to reach Obsidian (use a private tunnel for a cloud server).

Notes: The plugin uses a self-signed certificate; use the plain-HTTP port on a trusted network or a tunnel with a proper certificate.

### Ollama

App key `ollama`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Ollama address, API key (if the server needs one).

1. Nothing to set on the server. Run Ollama where the Daksha server can reach it (default `http://localhost:11434`; for a cloud server use a private tunnel).

### OpenAI

App key `openai`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at platform.openai.com/api-keys.

### OpenRouter

App key `openrouter`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at openrouter.ai/keys.

### PayPal

App key `paypal`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: Client id, Secret, Mode.

1. Nothing to set on the server. Each person creates a **REST app** at developer.paypal.com → Apps & Credentials, enables the **Transaction search** feature, and enters the client id and secret (choose Sandbox to try it).

Notes: PayPal only returns transactions from the last 31 days per request, and they can take a few hours to appear.

### Perplexity Search

App key `perplexity-search`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at perplexity.ai/account/api (Perplexity API).

Notes: Each search is billed to the person's Perplexity account.

### Philips Hue

App key `philips-hue`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Bridge address, Application key.

1. Nothing to set on the server. Find the bridge address in the Hue app (Settings → My Hue system → bridge) or at discovery.meethue.com.
2. In Daksha press the **link button** on the bridge, then enter the address and connect. Daksha receives its own key from the bridge (you can also paste an existing key).

Notes: Uses the bridge's local API over plain HTTP, so it must run on a trusted network.

### Qwen

App key `qwen`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** in Alibaba Cloud Model Studio (bailian.console.alibabacloud.com → API key). The international endpoint is used.

Notes: Mainland China accounts use dashscope.aliyuncs.com instead; use the Custom compatible API app for that.

### Razorpay

App key `razorpay`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Key id, Key secret.

1. Nothing to set on the server. Each person generates an **API key** in the Razorpay Dashboard (Account & Settings → API keys) and enters the key id and secret. Test-mode keys (`rzp_test_…`) are safe to try.

Notes: Amounts are shown in rupees. Daksha never stores the secret in plain text.

### Runway

App key `runway`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** in the Runway developer portal (dev.runwayml.com).

### Sentry

App key `sentry`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Auth token, Sentry address.

1. Nothing to set on the server. Each person creates a **personal auth token** in Sentry with `org:read`, `project:read` and `event:read` (add `event:write` to resolve issues).
2. In Daksha they paste the token (and the address of a self-hosted Sentry, if they use one).

### SharePoint

App key `sharepoint`. Sign-in: OAuth. Read-only.

**Server settings:** `SharePoint__OAuth__ClientId`, `SharePoint__OAuth__ClientSecret` (or reuse `Microsoft__OAuth__ClientId` / `ClientSecret`); optional `SharePoint__OAuth__RedirectUri` and `SharePoint__OAuth__FrontendUrl`.

1. Reuses the Microsoft app registration (`Microsoft__OAuth__ClientId` / `ClientSecret`), or set `SharePoint__OAuth__*`. Add the delegated Graph permissions **Sites.Read.All** and **Files.Read.All** (an administrator may need to consent).
2. Register the redirect URI `<API address>/api/integrations/sharepoint/callback` with the provider (many providers require HTTPS).

### SmartThings

App key `smartthings`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Personal access token.

1. Nothing to set on the server. Each person creates a **personal access token** at account.smartthings.com/tokens with the Devices (list, read, execute) and Scenes (list, execute) permissions.

### Sonos

App key `sonos`. Sign-in: OAuth. Read-only until full access is turned on.

**Server settings:** `Sonos__OAuth__ClientId`, `Sonos__OAuth__ClientSecret`; optional `Sonos__OAuth__RedirectUri` and `Sonos__OAuth__FrontendUrl`.

1. developer.sonos.com → **Create integration** (Control API). Copy the key (client id) and secret.
2. Add the redirect URI `<API address>/api/integrations/sonos/callback` (Sonos requires HTTPS).
3. Register the redirect URI `<API address>/api/integrations/sonos/callback` with the provider (many providers require HTTPS).

### Stripe

App key `stripe`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Secret or restricted key.

1. Nothing to set on the server. Each person creates a **restricted API key** in Stripe (Developers → API keys) with read access to Balance, Charges, Customers, Invoices and Payouts (and write for Customers, Products, Prices and Payment Links to use full access). Test keys (`rk_test_…`) are safe to try.

### Supabase

App key `supabase`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: Access token.

1. Nothing to set on the server. Each person creates a **personal access token** (Supabase dashboard → Account → Access Tokens).

### Together AI

App key `together-ai`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at api.together.ai/settings/api-keys.

### Tuya

App key `tuya`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Access ID, Access secret, Data centre.

1. platform.tuya.com → Cloud → **Create a cloud project** (Smart Home, your data centre). Copy the **Access ID** and **Access Secret**.
2. Devices → **Link App Account** and scan the QR code with the Smart Life / Tuya app so the project can see your devices. Subscribe the project to the *Smart Home Basic Service* API group.

Notes: Signed requests follow Tuya's documented HMAC-SHA256 scheme. The list-devices call needs the Smart Home Basic Service subscription.

### Vercel

App key `vercel`. Each person enters their own details. Read-only.

**Server settings:** none. Each person enters: Access token.

1. Nothing to set on the server. Each person creates an **access token** (Vercel → Account Settings → Tokens) and pastes it into Daksha.

### Vercel AI Gateway

App key `vercel-ai-gateway`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **AI Gateway API key** in the Vercel dashboard (AI Gateway → API Keys).

### vLLM

App key `vllm`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Server address, API key (if the server needs one).

1. Nothing to set on the server. Run `vllm serve <model>` (OpenAI-compatible, default port 8000) where the Daksha server can reach it. Pass `--api-key` if you protect it.

### Weather

App key `weather`. Free public service: no sign-in and no key. Read-only.

**Server settings:** none. Nothing to configure.

1. Nothing to set. Weather uses the free Open-Meteo service, which needs no key (for non-commercial use).

Notes: Open-Meteo's free service is for non-commercial use; for commercial use take one of its paid plans.

### WooCommerce

App key `woocommerce`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Store address, Consumer key, Consumer secret.

1. Nothing to set on the server. Each store owner creates **REST API keys** (WooCommerce → Settings → Advanced → REST API) with Read (or Read/Write) permission.
2. In Daksha they enter the store address (must be https), the consumer key and the consumer secret.

### Xero

App key `xero`. Sign-in: OAuth. Read-only until full access is turned on.

**Server settings:** `Xero__OAuth__ClientId`, `Xero__OAuth__ClientSecret`; optional `Xero__OAuth__RedirectUri` and `Xero__OAuth__FrontendUrl`.

1. developer.xero.com → My Apps → **New app** (Web app). Copy the client id and secret.
2. Scopes used: `accounting.transactions.read`, `accounting.contacts.read`, `accounting.settings.read` (read) and `accounting.contacts` (full access). Each person chooses one organisation when signing in.
3. Register the redirect URI `<API address>/api/integrations/xero/callback` with the provider (many providers require HTTPS).

### Z.AI

App key `zai`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: API key.

1. Nothing to set on the server. Each person creates an **API key** at z.ai (API keys page).

### Zapier

App key `zapier`. Each person enters their own details. Read-only until full access is turned on.

**Server settings:** none. Each person enters: Name, Webhook address.

1. Nothing to set on the server. In Zapier create a Zap that starts with **Webhooks by Zapier → Catch Hook** and copy its address (`https://hooks.zapier.com/hooks/catch/…`).
2. In Daksha enter a name and that address. Add more Zaps later in Manage → Preferences.

Notes: Only addresses on hooks.zapier.com are accepted. Daksha cannot read results back from Zapier; it only starts the trigger.

### Zoho Books

App key `zoho-books`. Sign-in: OAuth. Read-only until full access is turned on.

**Server settings:** `ZohoBooks__OAuth__ClientId`, `ZohoBooks__OAuth__ClientSecret`, `ZohoBooks__Region` (default `com`); optional `ZohoBooks__OAuth__RedirectUri` and `ZohoBooks__OAuth__FrontendUrl`.

1. Open the Zoho API Console for your data centre and create a **Server-based Application**. Copy the client id and secret.
2. Set `ZohoBooks__Region` to your data centre suffix (`in`, `com`, `eu`, `com.au`, `jp`).
3. Register the redirect URI `<API address>/api/integrations/zoho-books/callback` with the provider (many providers require HTTPS).

### Zoho Mail

App key `zoho-mail`. Sign-in: OAuth. Read-only until full access is turned on.

**Server settings:** `ZohoMail__OAuth__ClientId`, `ZohoMail__OAuth__ClientSecret`, `ZohoMail__Region` (default `com`); optional `ZohoMail__OAuth__RedirectUri` and `ZohoMail__OAuth__FrontendUrl`.

1. Open the Zoho API Console for your data centre (`api-console.zoho.in` for India, `.com`, `.eu`, `.com.au`, `.jp`) and create a **Server-based Application**. Copy the client id and secret.
2. Set `ZohoMail__Region` to your data centre suffix (`in`, `com`, `eu`, `com.au`, `jp`).
3. Register the redirect URI `<API address>/api/integrations/zoho-mail/callback` with the provider (many providers require HTTPS).

Notes: The Zoho account's data centre decides the region; mail for an India account lives on zoho.in.

### Zoom

App key `zoom`. Sign-in: OAuth. Read-only until full access is turned on.

**Server settings:** `Zoom__OAuth__ClientId`, `Zoom__OAuth__ClientSecret`; optional `Zoom__OAuth__RedirectUri` and `Zoom__OAuth__FrontendUrl`.

1. Zoom App Marketplace → Develop → **Build App** → General app (user-managed OAuth). Copy the client id and secret.
2. Add the scopes `meeting:read`, `user:read`, `recording:read` (read) and `meeting:write` (full access).
3. Register the redirect URI `<API address>/api/integrations/zoom/callback` with the provider (many providers require HTTPS).
<!-- END GENERATED APPS -->

---

## Desktop app (Windows and Mac): installers and signing

The desktop project is `apps/desktop` (see its README). Nothing is needed to run it for yourself. To produce installers:

1. GitHub → **Settings → Secrets and variables → Actions → Variables** → add `DAKSHA_APP_URL` = the address of your Daksha web app (for example `https://app.daksha.example.com`).
2. **Actions → Desktop installers → Run workflow.** Download `daksha-windows` (a `.exe` installer) and `daksha-macos` (`.dmg` and `.zip`) from the run.
3. Optional signing (removes the "unknown publisher" warnings), as repository **Secrets**:
   - `CSC_LINK` — your code-signing certificate, base64-encoded (`.pfx` for Windows, `.p12` Developer ID for Mac); `CSC_KEY_PASSWORD` — its password.
   - macOS notarisation: `APPLE_ID`, `APPLE_APP_SPECIFIC_PASSWORD`, `APPLE_TEAM_ID`.
4. The installers cannot be built on this Linux machine, so they have not been produced yet: the workflow does it on Windows and macOS runners.
