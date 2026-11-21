# Microsoft (Outlook mail, Outlook calendar, OneDrive, Azure DevOps)

One Microsoft sign-in powers four services. The user signs in once; one encrypted refresh token is stored per Microsoft
account, and each service can be switched on or off under **Apps → Microsoft → Manage**. Microsoft Teams is out of scope.

Daksha starts **read-only**, which is much easier for a company to approve. The user can turn on **full access** (send mail, change events and files) in Manage.

| Service | API | Read-only (default) | Full access |
|---|---|---|---|
| Outlook mail | Microsoft Graph | `Mail.Read` | `Mail.ReadWrite Mail.Send` |
| Outlook calendar | Microsoft Graph | `Calendars.Read` | `Calendars.ReadWrite` |
| OneDrive | Microsoft Graph | `Files.Read` | `Files.ReadWrite` |
| Azure DevOps | Azure DevOps REST | `499b84ac-1321-427f-aa17-267ca6975798/user_impersonation` | (same) |

Base scopes always requested: `openid profile email offline_access User.Read`. Adding a service later asks Microsoft only for
that service's permission (already-granted ones are not asked again). Azure DevOps is a different resource, so its access
token is obtained from the same refresh token.

## One-time setup (Microsoft Entra admin centre)
1. **App registrations → New registration.** Supported account types: *Accounts in any organizational directory and personal Microsoft accounts*.
   (Azure DevOps and shared mailboxes need a work/school account; personal accounts work for mail, calendar and OneDrive.)
2. **Authentication → Add a platform → Web.** Redirect URI: `https://<API_HOST>/api/integrations/microsoft/callback`
   (local: `http://localhost:5080/api/integrations/microsoft/callback`).
3. **Certificates & secrets → New client secret.** Copy the value.
4. **API permissions → Add:** Microsoft Graph delegated `Mail.Read`, `Mail.ReadWrite`, `Mail.Send`, `Calendars.Read`, `Calendars.ReadWrite`,
   `Files.Read`, `Files.ReadWrite`, `User.Read`, `offline_access`; and **Azure DevOps** delegated `user_impersonation`.
5. Configure the API (environment or user-secrets, **never commit**):

```
Microsoft__OAuth__ClientId=<application (client) id>
Microsoft__OAuth__ClientSecret=<secret value>
Microsoft__OAuth__Tenant=common            # or a tenant id to restrict to one organization
Microsoft__OAuth__RedirectUri=https://<API_HOST>/api/integrations/microsoft/callback
Microsoft__OAuth__FrontendUrl=https://<WEB_ORIGIN>/apps/microsoft
```

For Azure DevOps, the organization must allow Microsoft Entra OAuth (Organization settings → Policies → *Third-party application access via OAuth*) and be connected to the same Entra tenant.

## What it feeds
- **Apps page:** the four tiles, plus a "Your connected apps" row at the top with everything that is switched on.
- **Dashboard:** a Microsoft card (unread mail, next 7 days, recent files, assigned work items) once added from Manage.
- **Mail / OneDrive / Azure DevOps:** `/apps/microsoft`. Outlook events are merged into **Calendar** with the Google ones.
- **Assistant:** the web chat and the owner's own WhatsApp/Telegram chat answer from Outlook mail, calendar, OneDrive and Azure DevOps when the question is about them (read-only; the services must be on and the privacy setting allow sending data to the AI provider). Adding or changing calendar events from chat goes to Outlook when no Google calendar is connected, or when it is chosen.

## Endpoints (`/api/integrations/microsoft`)
`GET /status`, `POST /connect?features=mail,calendar,onedrive,devops`, `GET /callback`, `POST /disconnect`, `POST /features/disable`,
`GET /mail/messages`, `GET|PATCH /mail/messages/{id}`, `POST /mail/messages`, `GET /onedrive/files`, `GET /devops/summary`, `GET /dashboard`.
Calendar uses the shared `/api/v1/calendar/events` endpoints.

## When a company blocks it ("Need admin approval")
Many companies only let an administrator approve new apps (Daksha is also an *unverified publisher* until the app is verified in the Microsoft Partner programme).
The Connect card has a **Microsoft says "Need admin approval"?** helper: the user types their company domain, and Daksha makes a one-time
approval link and a ready message for their IT administrator (`GET /api/integrations/microsoft/admin-approval`). Once the administrator clicks **Accept**,
everyone in that company can sign in. The administrator lands on the public page `/approved`. The link uses the same redirect URI as sign-in, so nothing extra needs registering.

## Azure DevOps with a personal access token
Microsoft sign-in for Azure DevOps only works with work or school accounts. For a personal account (for example `@outlook.com`), or when the
organization does not allow Entra sign-in, the user can use a personal access token instead: Manage → *Azure DevOps with a token*. The token should have
**Work Items: Read** and **Project and Team: Read** only. It is checked against the organization, stored encrypted (credential `azure-devops-pat`), never returned
by the API, and used in place of Microsoft sign-in for Azure DevOps only. Endpoints: `POST|DELETE /api/integrations/microsoft/devops/token`.
