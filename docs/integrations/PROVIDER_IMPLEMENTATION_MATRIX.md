# Provider integration implementation and gap register

This register covers every requested provider plus the 13 additional messaging providers already added to PR #5. It is deliberately explicit: a provider appearing here is **not** evidence that its connector is implemented or production-ready.

## Current verified code status

- Telegram and Slack have adapter source files in this PR.
- The other 13 messaging providers currently have configuration scaffolding only; they do not yet have complete provider adapters.
- The requested productivity, storage, social, developer, cloud, commerce, finance, maps/travel, smart-home and automation providers below are not implemented in code by this register.
- Existing channel persistence/session routing is still blocked by the MongoDB channel-service/outbox TODOs. Live credentials and provider sandbox accounts have not been supplied, so no live connection is claimed.
- All credentials must be stored in a secret store or encrypted database fields. Never commit live secrets or echo them in management APIs.

## Delivery rule

For each provider, a connector is complete only when it has: (1) provider-specific auth and token refresh, (2) connection probe, (3) supported operations, (4) inbound webhook/polling/event handling where applicable, (5) signature/state validation, (6) capability metadata and per-account permissions, (7) rate-limit/retry/idempotency handling, (8) automated tests with mocks/sandbox, and (9) documentation. Unsupported operations must be reported as unsupported, not simulated.

## Messaging providers (13 requested earlier)

| Provider | Integration surface | Missing before production |
|---|---|---|
| Microsoft Teams | Bot Framework/Teams bot | Entra app, bot registration, tenant policy, OAuth token validation, proactive conversation reference storage |
| Discord | Discord Bot API + Gateway/interactions | Bot token, application public key, Gateway lifecycle/intents, interaction signature validation |
| Google Chat | Google Chat API | Cloud project, Chat app deployment, service account/OAuth and event request verification |
| Signal | signal-cli bridge | External bridge deployment, bridge authentication, device registration and bridge health checks |
| LINE | LINE Messaging API | Channel token/secret, webhook HMAC verification, reply/push quotas |
| Facebook Messenger | Meta Messenger Platform | Page token, app review, webhook verification/signature and permissions |
| Instagram Messaging | Meta Instagram Messaging API | Professional account, Meta app review, permissions, webhook subscription and policy eligibility |
| iMessage | macOS bridge integration | External macOS bridge, account pairing, bridge health and platform limitations |
| Matrix | Matrix Client-Server API | Homeserver, access token, sync loop, room membership and event de-duplication |
| Mattermost | Mattermost REST API/webhooks | Server URL, bot token, outgoing webhook verification and team/channel mapping |
| Feishu / Lark | Feishu Open Platform | App credentials, event verification/decryption, tenant token and message APIs |
| Twitch Chat | Twitch IRC/EventSub | OAuth scopes, IRC reconnect, EventSub subscription and webhook signature validation |
| SMS (Twilio) | Twilio Programmable Messaging | Account SID/token, sender number, E.164 validation and Twilio request signature verification |

## Additional provider-by-provider register

### Microsoft Outlook Mail

- **Integration surface:** Microsoft Graph Mail API.
- **Required setup / access:** Entra app registration; OAuth 2.0 authorization-code/device flow; Mail.Read or Mail.ReadWrite and Mail.Send; tenant consent; delta/webhook subscriptions.
- **Missing implementation:** OAuth token lifecycle, mailbox selection, send/reply/draft, delta sync, subscription renewal, notifications, throttling and integration tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Yahoo Mail

- **Integration surface:** Yahoo Mail API / OAuth where account/API access is available.
- **Required setup / access:** Yahoo developer app and OAuth client; API access availability and scopes must be confirmed for target account type.
- **Missing implementation:** Validate current API eligibility; OAuth; message listing/search/send; MIME handling; refresh/revocation; webhook or polling strategy.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Zoho Mail

- **Integration surface:** Zoho Mail API.
- **Required setup / access:** Zoho OAuth client, region-specific accounts host, scopes and organization/mailbox identifiers.
- **Missing implementation:** OAuth; region-aware endpoints; message/folder/search/send operations; pagination; notifications/polling; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Proton Mail

- **Integration surface:** Proton Mail Bridge or supported Proton API access.
- **Required setup / access:** Bridge deployment and credentials; supported API availability must be verified; avoid unsupported private endpoints.
- **Missing implementation:** Bridge connector, local/network security model, mailbox sync/send, health checks and compatibility tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Google Calendar

- **Integration surface:** Google Calendar API.
- **Required setup / access:** Google Cloud project, OAuth consent, Calendar scopes, redirect URIs, optional push channel configuration.
- **Missing implementation:** OAuth; list/create/update/delete events; free/busy; incremental sync; watch renewal; timezone handling; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Microsoft Outlook Calendar

- **Integration surface:** Microsoft Graph Calendar API.
- **Required setup / access:** Entra app registration, delegated/application permissions, tenant consent, redirect URIs.
- **Missing implementation:** OAuth; event CRUD; availability; delta sync; subscriptions/renewal; timezone handling; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Apple Calendar

- **Integration surface:** EventKit on Apple devices or supported CalDAV account integration.
- **Required setup / access:** Native app entitlements/user permission for EventKit, or CalDAV server/account credentials; server-side iCloud access constraints.
- **Missing implementation:** Choose supported device-side vs CalDAV architecture; permission UX; event CRUD/sync; recurrence/timezone tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Calendly

- **Integration surface:** Calendly API and webhooks.
- **Required setup / access:** OAuth/personal access token, event and user scopes, webhook subscription.
- **Missing implementation:** OAuth/token storage; event types, scheduled/cancelled events, invitees; webhook signature verification; retries/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Zoom

- **Integration surface:** Zoom REST API, Server-to-Server OAuth or user OAuth; webhooks.
- **Required setup / access:** Zoom Marketplace app, account scopes, client credentials and webhook secret.
- **Missing implementation:** OAuth; meetings CRUD; registrants; recordings; webhook signature validation; pagination/rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Google Drive

- **Integration surface:** Google Drive API.
- **Required setup / access:** Google Cloud OAuth client/service account, Drive scopes, Shared Drive policy.
- **Missing implementation:** OAuth; file metadata/download/upload; resumable uploads; changes feed; permissions; webhook renewal; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Google Docs

- **Integration surface:** Google Docs API plus Drive API.
- **Required setup / access:** Google Cloud OAuth and Docs/Drive scopes.
- **Missing implementation:** Document read/batchUpdate, export via Drive, permissions, revision handling and tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Google Sheets

- **Integration surface:** Google Sheets API plus Drive API.
- **Required setup / access:** Google Cloud OAuth and Sheets/Drive scopes.
- **Missing implementation:** Spreadsheet values and batch updates, sheet metadata, quotas, revision/conflict handling and tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Google Slides

- **Integration surface:** Google Slides API plus Drive API.
- **Required setup / access:** Google Cloud OAuth and Slides/Drive scopes.
- **Missing implementation:** Presentation read/batchUpdate, image/media handling, export via Drive, permissions and tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### OneDrive

- **Integration surface:** Microsoft Graph Drive API.
- **Required setup / access:** Entra app registration, Files.Read/Files.ReadWrite scopes, tenant consent.
- **Missing implementation:** OAuth; upload sessions; delta sync; sharing permissions; subscriptions; throttling/retry/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### SharePoint

- **Integration surface:** Microsoft Graph Sites/Lists/Drive APIs.
- **Required setup / access:** Entra app registration, Sites/Files permissions, tenant admin consent, site selection.
- **Missing implementation:** Site discovery; lists/documents CRUD; delta sync; permissions; subscription renewal; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Dropbox

- **Integration surface:** Dropbox API v2.
- **Required setup / access:** Dropbox developer app, OAuth scopes, redirect URI, team/admin consent if needed.
- **Missing implementation:** OAuth; files/folders CRUD; upload sessions; cursor-based sync; webhooks; shared links; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Box

- **Integration surface:** Box Platform APIs.
- **Required setup / access:** Box developer app, OAuth/JWT app auth, enterprise admin approval and scopes.
- **Missing implementation:** Auth lifecycle; files/folders; upload sessions; events stream; collaboration/permissions; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Notion

- **Integration surface:** Notion API.
- **Required setup / access:** Notion integration, OAuth or internal token, workspace connection and capabilities.
- **Missing implementation:** OAuth/install flow; pages/blocks/databases CRUD; pagination; rate limits; webhook support strategy; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Confluence

- **Integration surface:** Atlassian Confluence REST API.
- **Required setup / access:** Atlassian OAuth 2.0 (3LO) app, scopes, site/cloud ID.
- **Missing implementation:** OAuth; spaces/pages CRUD; CQL search; attachments; pagination; webhook strategy; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Airtable

- **Integration surface:** Airtable Web API.
- **Required setup / access:** Personal access token or OAuth app; base/table scopes.
- **Missing implementation:** OAuth/PAT; schema discovery; records CRUD; pagination; rate limits; webhook creation/refresh; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Facebook Pages

- **Integration surface:** Meta Graph API.
- **Required setup / access:** Meta app, Page access token, permissions, App Review and webhook subscriptions.
- **Missing implementation:** OAuth/Page selection; publish/read/manage posts; insights; webhook signature validation; permission review/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Instagram

- **Integration surface:** Instagram Graph API / Instagram Platform APIs.
- **Required setup / access:** Professional account, linked Page where required by chosen API, Meta app review and permissions.
- **Missing implementation:** Account discovery; media publishing; comments/mentions/insights; webhook verification; API eligibility matrix and tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### X

- **Integration surface:** X API.
- **Required setup / access:** Developer project/app, OAuth 2.0 or OAuth 1.0a credentials, paid/approved endpoint access as applicable.
- **Missing implementation:** Auth; posts/read/search/users; quotas/tier gates; webhook/stream strategy; error mapping/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### LinkedIn

- **Integration surface:** LinkedIn Marketing/Community Management APIs.
- **Required setup / access:** LinkedIn developer app, product approval, OAuth scopes and organization authorization.
- **Missing implementation:** OAuth; profile/org access; posts/comments; restricted API approval path; rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### YouTube

- **Integration surface:** YouTube Data API.
- **Required setup / access:** Google Cloud project, OAuth consent, YouTube scopes; API quota.
- **Missing implementation:** OAuth; channels/videos/playlists/comments; upload resumable flow; quota accounting; push notifications/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### TikTok

- **Integration surface:** TikTok for Developers APIs.
- **Required setup / access:** TikTok developer app, product approval, OAuth scopes and redirect URI.
- **Missing implementation:** OAuth; approved display/content posting APIs; publish status; webhook/polling; regional/permission gates/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Reddit

- **Integration surface:** Reddit API.
- **Required setup / access:** Reddit app registration, OAuth client, user-agent and API policy compliance.
- **Missing implementation:** OAuth; posts/comments/subreddits; rate limiting; moderation scopes; polling strategy; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Pinterest

- **Integration surface:** Pinterest API v5.
- **Required setup / access:** Pinterest developer app, OAuth scopes, account approval.
- **Missing implementation:** OAuth; boards/pins CRUD; analytics where approved; pagination/rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Mastodon

- **Integration surface:** Mastodon REST API.
- **Required setup / access:** Instance URL and per-instance OAuth app/user token.
- **Missing implementation:** Instance discovery; OAuth per instance; statuses/media/notifications; rate limits; streaming reconnect/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Bluesky

- **Integration surface:** AT Protocol / Bluesky APIs.
- **Required setup / access:** App password or OAuth support per current protocol/client capability.
- **Missing implementation:** Session/token lifecycle; records/feed/post operations; blob uploads; rate limits; auth migration/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### GitHub

- **Integration surface:** GitHub REST API.
- **Required setup / access:** Personal access token for initial connection, or GitHub App installation token for production multi-tenant use.
- **Implemented in this PR:** Connection probe (`GET /user`), repository listing, open issue listing (excluding pull requests returned by GitHub's shared issues endpoint), issue creation, and open pull-request listing. HTTP client is registered with a fixed GitHub API base URL and API version header.
- **Missing implementation:** GitHub App JWT/install token lifecycle, OAuth installation flow, webhook signature verification and event routing, pagination cursors, rate-limit/backoff handling, richer Actions/Checks/Projects APIs, encrypted per-account token persistence, API endpoints/UI, and automated integration tests.
- **Code status:** Partial implementation. See `services/platform/Daksh.Platform/Integrations/DeveloperPlatforms/GitHubIntegration.cs`. Not production-complete.

### GitLab

- **Integration surface:** GitLab.com REST API v4.
- **Required setup / access:** Personal, project or group access token with the required API scope. Self-managed GitLab requires a separately configured trusted instance base URL.
- **Implemented in this PR:** Connection probe, membership project listing, and issue creation by numeric project ID. Token is supplied per call and is not stored by this client.
- **Missing implementation:** OAuth install flow, token storage/rotation, self-managed host support, merge-request and pipeline operations, webhook signature validation/event routing, cursor pagination, rate-limit handling, API endpoints/UI, and automated tests.
- **Code status:** Partial implementation. See `services/platform/Daksh.Platform/Integrations/DeveloperPlatforms/GitLabIntegration.cs`. Not production-complete.

### Bitbucket

- **Integration surface:** Bitbucket Cloud REST API and webhooks.
- **Required setup / access:** Atlassian OAuth consumer or supported workspace/repository token.
- **Missing implementation:** OAuth; repos/PRs/issues/pipelines; webhook verification; workspace permissions; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Azure DevOps

- **Integration surface:** Azure DevOps REST APIs and service hooks.
- **Required setup / access:** Microsoft Entra OAuth app or supported PAT; organization and scopes.
- **Missing implementation:** OAuth; projects/repos/work items/pipelines; service hook validation; pagination/API versioning/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Jira

- **Integration surface:** Jira Cloud REST API and webhooks.
- **Required setup / access:** Atlassian OAuth 2.0 (3LO), scopes, site/cloud ID.
- **Missing implementation:** OAuth; issues/projects/search/transitions; webhook registration/validation; pagination/rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Sentry

- **Integration surface:** Sentry API and webhooks.
- **Required setup / access:** Sentry OAuth app or auth token; organization/project scopes.
- **Missing implementation:** Issues/events/releases/projects; webhook signature verification; pagination/rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Docker Hub

- **Integration surface:** Docker Hub API and webhooks.
- **Required setup / access:** Docker Hub account/token; organization permissions.
- **Missing implementation:** Auth; repositories/tags/manifests metadata; webhook validation; rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Kubernetes

- **Integration surface:** Kubernetes API.
- **Required setup / access:** Cluster endpoint, service-account token or kubeconfig, CA trust and RBAC.
- **Missing implementation:** Secure credential provisioning; discovery; resource CRUD/watch; namespace/RBAC boundaries; reconnect and cluster tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### AWS

- **Integration surface:** AWS SDK/service APIs; IAM roles preferred.
- **Required setup / access:** IAM role/STS or access keys, region, least-privilege policies.
- **Missing implementation:** Credential chain; service-specific adapters; pagination/retries; event integrations; audit and security tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Microsoft Azure

- **Integration surface:** Azure SDK/ARM APIs.
- **Required setup / access:** Entra service principal or managed identity, subscription/tenant and RBAC.
- **Missing implementation:** Token acquisition; resource-specific clients; subscription discovery; retries; event integration; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Google Cloud

- **Integration surface:** Google Cloud APIs/SDK.
- **Required setup / access:** Workload Identity Federation or service account; project and IAM roles.
- **Missing implementation:** Credential chain; service-specific clients; project discovery; Pub/Sub events; retries/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Cloudflare

- **Integration surface:** Cloudflare API.
- **Required setup / access:** API token with scoped permissions and account/zone identifiers.
- **Missing implementation:** Zone/account discovery; DNS/cache/Workers operations by capability; audit logging; rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Vercel

- **Integration surface:** Vercel REST API and webhooks.
- **Required setup / access:** Vercel OAuth integration or scoped access token; team/project IDs.
- **Missing implementation:** Projects/deployments/domains/logs; webhook validation; pagination/rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Supabase

- **Integration surface:** Supabase Management API and project APIs.
- **Required setup / access:** Access token for management API; project URL and scoped service credentials.
- **Missing implementation:** Project discovery; management operations vs data-plane separation; secret handling; webhooks/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Firebase

- **Integration surface:** Firebase Admin SDK and Google APIs.
- **Required setup / access:** Service account/Workload Identity; project ID and IAM roles.
- **Missing implementation:** Auth/Firestore/FCM/Storage adapters by scope; event triggers; credential isolation; emulator/integration tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Shopify

- **Integration surface:** Shopify Admin GraphQL/REST APIs and webhooks.
- **Required setup / access:** Shopify app, OAuth, shop domain, approved scopes and webhook secret.
- **Missing implementation:** OAuth/install/uninstall; products/orders/inventory; webhook HMAC; API versioning/rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### WooCommerce

- **Integration surface:** WooCommerce REST API and webhooks.
- **Required setup / access:** Store URL, consumer key/secret, HTTPS and API permissions.
- **Missing implementation:** Credential validation; products/orders/customers; webhook signature; pagination/version compatibility/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### eBay

- **Integration surface:** eBay Sell/Buy APIs.
- **Required setup / access:** Developer account, application keys, OAuth user consent and marketplace scopes.
- **Missing implementation:** OAuth; inventory/orders/listings; marketplace-specific requirements; notifications; sandbox and production tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Amazon Selling Partner API

- **Integration surface:** Amazon SP-API.
- **Required setup / access:** Developer registration, LWA app, seller authorization, IAM role and restricted-data approval where required.
- **Missing implementation:** LWA refresh/token flow; AWS signing; region/marketplace routing; reports/orders/catalog; rate limits and compliance tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Razorpay

- **Integration surface:** Razorpay APIs and webhooks.
- **Required setup / access:** Razorpay account, key ID/secret, webhook secret and live/test mode.
- **Missing implementation:** Orders/payments/refunds; webhook signature verification and idempotency; secret rotation; reconciliation/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Stripe

- **Integration surface:** Stripe API and webhooks.
- **Required setup / access:** Stripe account, restricted API key/OAuth Connect as applicable, webhook signing secret.
- **Missing implementation:** Customers/products/prices/payment intents/subscriptions; signature verification; idempotency; Connect; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### PayPal

- **Integration surface:** PayPal REST APIs and webhooks.
- **Required setup / access:** PayPal developer app client ID/secret; sandbox/live environment.
- **Missing implementation:** OAuth; orders/captures/refunds; webhook signature verification; idempotency; environment tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### QuickBooks Online

- **Integration surface:** Intuit QuickBooks Online Accounting API.
- **Required setup / access:** Intuit developer app, OAuth 2.0, company realm ID and scopes.
- **Missing implementation:** OAuth refresh rotation; company selection; accounting entities; CDC/webhooks; sandbox tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Xero

- **Integration surface:** Xero Accounting API.
- **Required setup / access:** Xero developer app, OAuth 2.0, tenant connection and scopes.
- **Missing implementation:** OAuth refresh; tenant discovery; contacts/invoices/payments; webhooks; rate limits/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Zoho Books

- **Integration surface:** Zoho Books API.
- **Required setup / access:** Zoho OAuth client, data-center region, organization ID and scopes.
- **Missing implementation:** Region-aware OAuth; organizations; invoices/expenses/contacts; webhooks/polling; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Google Maps Platform

- **Integration surface:** Google Maps Platform APIs.
- **Required setup / access:** Google Cloud project, billing enabled, restricted API key or OAuth depending API.
- **Missing implementation:** Key restrictions; Maps/Places/Routes/Geocoding capability adapters; quota/billing controls; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Mapbox

- **Integration surface:** Mapbox APIs.
- **Required setup / access:** Mapbox access token with scopes and allowed URL restrictions.
- **Missing implementation:** Geocoding/search/directions/tiles adapters; token scopes; quota and attribution handling; tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Amadeus

- **Integration surface:** Amadeus Self-Service APIs.
- **Required setup / access:** Amadeus developer account, API key/secret, test/production environment.
- **Missing implementation:** OAuth client credentials; flight/hotel APIs by product; booking eligibility constraints; quotas and tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Booking.com

- **Integration surface:** Booking.com Connectivity APIs.
- **Required setup / access:** Partner/affiliate approval and property/affiliate credentials; API access approval.
- **Missing implementation:** Confirm program eligibility; auth; availability/content/reservations APIs; webhook/reconciliation and certification tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Uber

- **Integration surface:** Uber APIs / Mobility and other approved products.
- **Required setup / access:** Uber developer app, product access approval, OAuth scopes.
- **Missing implementation:** Confirm API product eligibility; OAuth; estimates/trips/receipts or business APIs; webhook and policy tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### IRCTC

- **Integration surface:** Official IRCTC partner integrations only.
- **Required setup / access:** Authorized partner relationship and approved credentials; public access is restricted.
- **Missing implementation:** Obtain written API/partner authorization; use approved endpoints only; security/compliance and certification; no scraping workaround.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### DigiLocker

- **Integration surface:** DigiLocker APIs / issuer-requester integration.
- **Required setup / access:** Registered requester/issuer organization, approval, client credentials and consent flow.
- **Missing implementation:** Organization onboarding; OAuth/consent; document fetch/share; audit/retention controls; certification tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Bhashini

- **Integration surface:** BHASHINI APIs.
- **Required setup / access:** Platform onboarding/API key and approved service/domain configuration.
- **Missing implementation:** Auth; language detection/translation/ASR/TTS adapters; payload limits; language-quality and latency tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### ONDC

- **Integration surface:** ONDC Network protocols.
- **Required setup / access:** Network participant role, registry onboarding, signing keys, certificates and domain compliance.
- **Missing implementation:** Participant onboarding; protocol signing/verification; search/select/init/confirm/status/cancel flows; certification and sandbox tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Home Assistant

- **Integration surface:** Home Assistant REST/WebSocket APIs.
- **Required setup / access:** Instance URL and long-lived access token; network reachability.
- **Missing implementation:** Auth; entity/service discovery; state subscriptions; reconnect; permission boundaries and tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Philips Hue

- **Integration surface:** Hue Bridge API / supported cloud APIs.
- **Required setup / access:** Bridge pairing/application key or supported account API access.
- **Missing implementation:** Bridge discovery/pairing; local API auth; lights/scenes/groups; event stream/reconnect; hardware tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### SmartThings

- **Integration surface:** SmartThings API and webhooks.
- **Required setup / access:** Samsung developer account, OAuth/API token, app registration and subscriptions.
- **Missing implementation:** OAuth; devices/capabilities/commands; lifecycle events; webhook validation; device simulation/tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Tuya

- **Integration surface:** Tuya IoT Cloud APIs.
- **Required setup / access:** Tuya developer project, data-center region, access ID/secret and device authorization.
- **Missing implementation:** Region-aware auth; devices/scenes; token refresh; webhook/polling; signing and hardware tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### IFTTT

- **Integration surface:** IFTTT platform/Webhooks integrations.
- **Required setup / access:** IFTTT service/platform access and webhook key; partner access may be required.
- **Missing implementation:** Confirm current platform access; trigger/action definitions; authentication; rate limits and end-to-end tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Zapier

- **Integration surface:** Zapier Platform integrations / CLI.
- **Required setup / access:** Zapier developer account, integration app and OAuth/API-key auth design.
- **Missing implementation:** Triggers/actions/searches; polling/webhooks; OAuth refresh; test fixtures and publishing review.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### n8n

- **Integration surface:** n8n public API and webhooks.
- **Required setup / access:** n8n instance URL, API key and enabled API access.
- **Missing implementation:** Instance/version detection; workflow/credential metadata; execution APIs; webhook auth; self-hosted compatibility tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### Make

- **Integration surface:** Make API and custom webhooks.
- **Required setup / access:** Make organization/team access and API token; API availability by plan.
- **Missing implementation:** Scenario/webhook management; auth; organization scoping; plan limits; integration tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### MCP servers

- **Integration surface:** Model Context Protocol client transport.
- **Required setup / access:** Server URL/stdio command, transport type, auth configuration and trust policy.
- **Missing implementation:** MCP handshake; tool/resource/prompt discovery; stdio/HTTP transports; OAuth; permission approval; sandboxing and conformance tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

### OpenClaw Plugins

- **Integration surface:** OpenClaw plugin/extension interface.
- **Required setup / access:** OpenClaw installation, plugin manifest, runtime and permission model.
- **Missing implementation:** Confirm stable public plugin contract; discovery/install lifecycle; compatibility adapter; sandboxing; permission review and tests.
- **Code status:** Not implemented; this section is a gap register, not a working connector.

## Cross-cutting platform work

1. **Connector SDK:** common contracts for provider identity, auth setup, health probe, capabilities, actions, triggers, webhook handling and diagnostics.
2. **Credential vault:** encrypted-at-rest credentials, key rotation, redaction, per-tenant isolation and least-privilege scopes.
3. **Connection lifecycle:** connect, callback/state validation, refresh, revoke, reconnect and health monitoring.
4. **Capability model:** metadata-driven UI with supported/enabled/permission-denied states per account and feature.
5. **Execution model:** durable jobs, idempotency keys, retries with backoff, rate-limit handling, dead-letter queue and audit trail.
6. **Webhook gateway:** raw-body signature verification, timestamp/replay protection, tenant routing and event deduplication.
7. **Persistence and routing:** finish MongoDB-backed account, event, subscription and outbox persistence before claiming end-to-end agent workflows.
8. **Testing:** contract tests, provider mocks, sandbox tests, webhook fixtures, OAuth callback tests and negative security tests.
9. **Compliance:** provider terms, app review, regional availability, data retention, user consent and sensitive-data minimization.

## Recommended implementation order

1. Complete channel runtime persistence/routing and a generic connector contract.
2. Implement OAuth-heavy, broadly useful connectors: Google Workspace, Microsoft Graph, GitHub/GitLab, Notion/Confluence, Slack/Teams/Discord.
3. Add storage, cloud, payments and commerce connectors with strict secret scopes and idempotent writes.
4. Add social publishing and messaging connectors subject to app-review approvals and platform-specific restrictions.
5. Add travel, India government/network integrations, smart-home and automation platforms after partner/device credentials and sandbox access are available.
6. Add MCP and OpenClaw plugin runtimes only behind explicit trust, tool approval and sandbox boundaries.

## Important access constraints

Some providers require app review, partner onboarding, paid plans, organization approval, device bridges or region-specific access. IRCTC, ONDC, DigiLocker, Booking.com, Uber, Amazon SP-API, Meta, LinkedIn and TikTok must not be represented as generally available until the relevant program approval is obtained. Signal and iMessage require bridge-based designs rather than pretending they expose equivalent official server-side bot APIs.
