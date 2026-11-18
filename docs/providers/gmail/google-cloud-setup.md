# Google Cloud setup

1. Create a Google Cloud project and enable the **Gmail API** (and **Cloud Pub/Sub API** for push).
2. **OAuth consent screen**: set app name, support email, authorised domains, privacy policy (`/legal/privacy`) and terms (`/legal/terms`) URLs. Add scopes from
   [oauth-and-scopes.md](oauth-and-scopes.md). In *Testing* mode only listed test users can connect (and tokens expire after 7 days). Publishing to everyone requires
   Google verification (restricted scopes → security assessment).
3. **OAuth client** (Web application): authorised redirect URI `https://<api-host>/api/integrations/gmail/callback` (local: `http://localhost:5080/api/integrations/gmail/callback`).
4. Configure the API (environment variables or user-secrets — **do not commit**): `Gmail__OAuth__ClientId`, `Gmail__OAuth__ClientSecret`, `Gmail__OAuth__RedirectUri`, `Gmail__OAuth__FrontendUrl`.
5. **Push (optional but recommended):**
   - Create a Pub/Sub topic; grant `gmail-api-push@system.gserviceaccount.com` the **Pub/Sub Publisher** role on it.
   - Create a **push subscription** to `https://<api-host>/api/integrations/gmail/push?token=<secret>` (HTTPS required).
   - Set `Gmail__PubSub__TopicName=projects/<project>/topics/<topic>` and `Gmail__PubSub__VerificationToken=<secret>`.
   - The worker starts/renews the watch daily; without these the API polls at each account's interval.
6. Set `Security__Encryption__*` (token encryption key) and a production MongoDB connection; deploy a single API instance group with the hosted worker enabled.

Not done by the repo: creating any of the above, credentials, verification submission. Until `Gmail:OAuth:*` is set, `POST /connect` returns 503.
Note: the committed `appsettings.json` should not hold real client secrets — move any real values to secrets and rotate any that were committed.
