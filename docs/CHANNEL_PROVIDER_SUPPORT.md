# Channel provider integration plan

All providers are independently disabled by default. Credentials are placeholders only; put real secrets in deployment environment variables or a secret store, never commit them to appsettings.

## Provider support model

| Provider | Integration approach | Important requirements |
|---|---|---|
| Telegram | Official Bot API | Bot token, webhook secret token |
| Slack | Official Web API + Events API | Bot token, signing secret |
| Microsoft Teams | Microsoft Bot Framework / Teams bot | Entra app registration, bot credentials, tenant configuration |
| Discord | Discord Bot API + Gateway / interactions | Bot token; application public key for signed interactions |
| Google Chat | Google Chat API | Google Cloud service account / OAuth and Chat app deployment |
| Signal | External signal-cli REST bridge | Self-hosted bridge URL and bridge authentication; no official hosted bot API |
| LINE | LINE Messaging API | Channel access token and channel secret |
| Facebook Messenger | Meta Messenger Platform | Facebook app, Page access token, app secret, webhook verification token and permissions |
| Instagram Messaging | Meta Instagram Messaging API | Professional Instagram account connected to a Facebook Page, app review/permissions and webhook subscription |
| iMessage | External macOS bridge | Bridge URL and bridge authentication; no generally available official server-side bot API |
| Matrix | Matrix Client-Server API | Homeserver URL, access token and user ID |
| Mattermost | Mattermost REST API + outgoing webhooks | Server URL, bot token and webhook secret |
| Feishu / Lark | Feishu Open Platform | App ID/secret, event verification token and optional encrypt key |
| Twitch Chat | Twitch IRC / EventSub | Twitch app credentials, user access token and channel |
| SMS (Twilio) | Twilio Programmable Messaging | Account SID, Auth Token, sender number and inbound webhook signature validation |

## Configuration status

The appsettings and .env.example sections are present for all providers above, with every integration disabled and credentials marked NEED_TO_GET.

## Implementation status

Configuration scaffolding is not the same as a working adapter. Telegram and Slack have adapter implementations in the current PR. The additional providers still require provider-specific adapters, inbound verification/parsing, outbound delivery, connection probes, tests, and per-feature capability metadata before they should be shown as connected or enabled in the management UI.

Signal and iMessage are explicitly bridge integrations. Daksh must not collect or display them as connected until a compatible bridge is configured and its health check succeeds.

## Management UI requirements

The channel management UI should be driven by adapter metadata, not hard-coded provider assumptions. For each configured account it should show connection state, last successful probe, last inbound/outbound activity, enabled capabilities, and errors. Each capability should be independently configurable where the provider supports it. Unsupported capabilities must be disabled with an explanation rather than silently accepted.

Credentials must be encrypted at rest. UI updates should be persisted and applied without redeploying. A provider's global Enabled flag must not override disabled per-capability settings.
