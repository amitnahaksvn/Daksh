# OAuth and scopes

- Authorization-code flow with **PKCE (S256)** and a one-time, hashed `state` (10-minute TTL). The code verifier is encrypted at rest.
- Tokens are stored encrypted (`ISecretProtector`) in `integrationCredentials`; the API never returns them and logs never contain them.
- Access tokens refresh automatically (also on a 401). A rejected refresh marks the connection as errored and asks the user to reconnect.
- A missing refresh token aborts the connection with `refresh_token_missing`.
- Incremental authorization: `POST /connect?features=modify,compose,labels,settings,full` adds only the requested scopes
  (`include_granted_scopes=true`). Granted scopes are recorded per account from Google's token response.

| Feature (permission) | Scope requested |
|---|---|
| Read, search, read attachments | `gmail.readonly` (on connect) |
| Send, reply, forward | `gmail.send` (on connect) |
| Drafts | `gmail.compose` (upgrade `compose`) |
| Archive, trash, star, read/unread, apply labels | `gmail.modify` (upgrade `modify`) |
| Create/rename/delete labels | `gmail.labels` (upgrade `labels`; `gmail.modify` also satisfies it) |
| Filters, vacation, language, send-as signature | `gmail.settings.basic` (upgrade `settings`) |
| Permanent delete | `https://mail.google.com/` (upgrade `full`) – restricted |

Not requested: `gmail.settings.sharing` (needed to add/verify forwarding addresses and change auto-forwarding) — see [limitations.md](limitations.md).
Accounts connected before scope tracking are treated as read + send only until they approve more.

When a feature needs a scope that isn't granted the API answers `403` with `code: scope_upgrade_required` and `requiredScope`, and the UI shows
an "Approve in Google" prompt that explains why. `GET /accounts/{id}/permissions` returns the mapping and the reason for each scope.

Verification: `gmail.readonly`, `gmail.modify`, `gmail.compose` and `mail.google.com` are **restricted** scopes; `gmail.send`, `gmail.labels`
and `gmail.settings.basic` are **sensitive**. Production use by the public requires Google OAuth verification (and, for restricted scopes, a
security assessment). See [google-cloud-setup.md](google-cloud-setup.md).
