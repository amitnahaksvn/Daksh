# Slack

Daksha connects to Slack with Slack's OAuth (v2) and acts **as the person** (a *user token*), so it only sees what they can see.
One `slack` connection and one encrypted token are stored per Slack workspace; a person can connect several workspaces.

Daksha starts **read-only**. **Full access** (Apps → Slack → Manage → General) adds posting and marking conversations read.

| | User token scopes |
|---|---|
| Read-only (default) | `channels:read groups:read im:read mpim:read channels:history groups:history im:history mpim:history users:read search:read` |
| Full access | adds `chat:write im:write channels:write groups:write mpim:write` |

## One-time setup (api.slack.com/apps → Create New App → From scratch)
1. **OAuth & Permissions → Redirect URLs:** `https://<API_HOST>/api/integrations/slack/callback`
   (local: `http://localhost:5080/api/integrations/slack/callback`; Slack may require HTTPS, in which case use a tunnel).
2. **OAuth & Permissions → User Token Scopes:** add every scope in the table above. Leave **token rotation off**.
3. **Basic Information:** copy the *Client ID*, *Client Secret* and *Signing Secret*.
4. *(Optional, for live DMs and mentions)* **Event Subscriptions:** turn on, set the Request URL to
   `https://<API_HOST>/api/integrations/slack/events` (it must be reachable from the internet), and under *Subscribe to events on behalf of users*
   add `message.im`, `message.mpim`, `message.channels` and `message.groups`.
5. Configure the API (environment or user-secrets, **never commit**):

```
Slack__OAuth__ClientId=<client id>
Slack__OAuth__ClientSecret=<client secret>
Slack__OAuth__RedirectUri=https://<API_HOST>/api/integrations/slack/callback
Slack__OAuth__FrontendUrl=https://<WEB_ORIGIN>/apps/slack
Slack__SigningSecret=<signing secret>        # only for the Events API (step 4)
```

Without the Events API everything still works (reading, searching, posting, the assistant); the dashboard tile then lists the people
and channels you talk to instead of a live feed of new direct messages and mentions.

## What it feeds
- **Apps page:** the Slack tile and a Manage popup: General (workspaces, default workspace, full access, dashboard), Messages, Notifications, Dashboard.
- **/apps/slack:** channels and direct messages, the open conversation with older messages and threads, search, and (with full access) posting and replying.
- **Dashboard:** an optional Slack tile per workspace.
- **Assistant:** answers questions about recent Slack direct messages, mentions and searches when asked.

## Security
- Tokens are encrypted at rest (`ISecretProtector`) and removed (and revoked at Slack) on disconnect.
- The Events endpoint verifies Slack's `X-Slack-Signature` (HMAC-SHA256, five-minute window) and refuses unsigned or replayed requests.
- Inbound events are kept for 14 days, only for DMs and mentions of a connected user, and removed on disconnect.

## API
`/api/integrations/slack/…`: `status`, `connect`, `callback`, `disconnect`, `write`, `settings`, `channels`, `channels/{id}/messages` (GET history, POST send),
`channels/{id}/replies`, `channels/{id}/read`, `search`, `dashboard`, `activity/read`, `events` (Slack → Daksha).
