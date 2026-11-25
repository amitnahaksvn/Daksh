# Google Drive, Docs, Sheets and Slides

These four services ride on the **same Google sign-in as Gmail and Calendar**. There is no separate OAuth client: the extra
permissions are asked for through Gmail's `/api/integrations/gmail/connect?features=…`, and Google remembers what was already
granted (`include_granted_scopes`). The granted scopes are stored on the Gmail account; per-user choices (which services are on,
full access, dashboard options) live in `googleWorkspaceSettings`. One `google-drive` connection represents the setup so it can be
pinned to the dashboard like any app.

Daksha starts **read-only**. **Full access** (Apps → Google Drive → Manage → General) lets it create Docs and Sheets and add text
and rows. It never deletes anything, and Slides is always read-only.

| Service | API | Read-only (default) | Full access |
|---|---|---|---|
| Drive | Drive v3 | `drive.readonly` | `drive.file` |
| Docs | Docs v1 | `documents.readonly` | `documents` |
| Sheets | Sheets v4 | `spreadsheets.readonly` | `spreadsheets` |
| Slides | Slides v1 | `presentations.readonly` | (read-only) |

## One-time setup (Google Cloud console)
Use the same OAuth client Gmail already uses (`Gmail__OAuth__ClientId` / `Gmail__OAuth__ClientSecret`), then:
1. **APIs & Services → Library:** enable the Google Drive, Docs, Sheets and Slides APIs.
2. **OAuth consent screen → Data access:** add the scopes in the table above. `drive.readonly` is a *restricted* scope, so a public
   launch needs Google's verification (and a security assessment); testing with your own accounts needs none.

No new environment variables are needed.

## What it feeds
- **Apps page:** four tiles (Drive, Docs, Sheets, Slides) that open one Manage popup: General (services, full access, dashboard),
  Drive, Docs, Sheets and Slides tabs.
- **/apps/google-drive:** browse (recent, My Drive, shared, starred), search, filter by type, open Docs, Sheets and Slides in Daksha,
  and — with full access — create documents and spreadsheets and add text or rows.
- **Dashboard:** an optional Google Drive tile listing the files the user chose (recent, shared, starred or My Drive).
- **Assistant:** answers questions about Drive files when asked (it searches Drive for the words in the question).

## API
`/api/integrations/google-workspace/…`: `status`, `enable`, `services/disable`, `write`, `disconnect`, `settings`, `drive/files`,
`docs/{id}` (+ `POST docs`, `POST docs/{id}/append`), `sheets/{id}` (+ `POST sheets`, `POST sheets/{id}/append`), `slides/{id}`, `dashboard`.
When Google has not been asked for a permission yet, the API answers `409` with `code=scope_upgrade_required` and the feature list to
connect with; the web app then opens the Google sign-in popup and retries.
