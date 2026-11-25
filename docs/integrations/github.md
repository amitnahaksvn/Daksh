# GitHub

GitHub connects with an **OAuth App** sign-in (a user token, so Daksha acts as the person). One `github` connection and one encrypted
token per person. The Apps tile opens one Manage popup: General (access level, private repositories, dashboard), Work, Notifications.

Read-only by default (`read:user notifications read:org`). **Private repositories** adds `repo` (GitHub has no read-only scope for
private repos). **Full access** (comment on issues and pull requests, mark notifications read) adds `public_repo`, or relies on `repo`.

## One-time setup (github.com → Settings → Developer settings → OAuth Apps → New OAuth App)
- Homepage URL: your web origin. **Authorization callback URL:** `https://<API_HOST>/api/integrations/github/callback`
  (local: `http://localhost:5080/api/integrations/github/callback`).
- Generate a client secret. If the app should be usable by people in a company's GitHub organization, that organization's owner must
  approve it (Organization settings → Third-party access).

```
GitHub__OAuth__ClientId=
GitHub__OAuth__ClientSecret=
GitHub__OAuth__RedirectUri=https://<API_HOST>/api/integrations/github/callback
GitHub__OAuth__FrontendUrl=https://<WEB_ORIGIN>/apps/github
```

## What it feeds
- **/apps/github:** pull requests and issues (review requested, assigned, created, mentioning me; open/closed), open any item to read it
  and comment (full access), notifications, repositories.
- **Dashboard tile:** the list you chose plus unread notifications. **Assistant:** reviews waiting and assigned items.
- Disconnect revokes the grant at GitHub.

API: `/api/integrations/github/…` `status connect callback disconnect write settings items items/{owner}/{repo}/{n}(/comments) notifications(/read) repos dashboard`.
