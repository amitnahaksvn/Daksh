# Notion

Notion connects with a **public integration** (OAuth). The person chooses which pages and databases to share on Notion's consent
screen; Daksha sees only those. One `notion` connection and one encrypted token per person (tokens do not expire).

What Daksha may do is set by the *capabilities* on the integration in Notion (read, update, insert content). **Full access** in
Manage is Daksha's own switch for creating pages and adding text, so it needs no new sign-in, but it needs the Update and Insert
capabilities enabled on the integration.

## One-time setup (notion.so/my-integrations → New integration → type **Public**)
- **Redirect URI:** `https://<API_HOST>/api/integrations/notion/callback` (local: `http://localhost:5080/api/integrations/notion/callback`).
- **Capabilities:** Read content, Update content, Insert content; User information: *Read user information including email addresses*.
- A public integration also needs the company name, website, privacy-policy and terms URLs (use Daksha's).

```
Notion__OAuth__ClientId=        # OAuth client ID
Notion__OAuth__ClientSecret=    # OAuth client secret
Notion__OAuth__RedirectUri=https://<API_HOST>/api/integrations/notion/callback
Notion__OAuth__FrontendUrl=https://<WEB_ORIGIN>/apps/notion
```

## What it feeds
- **/apps/notion:** search shared pages and databases, read a page, view database rows, and (full access) create pages and add text.
- **Dashboard tile:** recently edited pages or databases. **Assistant:** finds pages matching a question and reads the best match.
- Notion has no token-revoke API: Disconnect makes Daksha forget the token; remove the integration in Notion → Settings → Connections to cut access fully.

API: `/api/integrations/notion/…` `status connect callback disconnect write settings search pages/{id}(/append) pages databases/{id} dashboard`.
