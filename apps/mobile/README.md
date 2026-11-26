# Daksh Mobile

Flutter (iOS/Android) client for the Daksh API. See `docs/SETUP.md` and `docs/MOBILE_PLATFORM_LIMITS.md`.

`flutter pub get && flutter analyze && flutter test`

## What is in the app

The mobile app has the same pages as the web app: the assistant (with chat history), dashboard, **Apps**, chats (WhatsApp, Telegram), email, calendar, tasks,
lists, reminders, notifications/activity, profile (details, password, "I'm busy") and privacy.

**Apps** lists every app. The apps that sign in once draw themselves from what the server tells them (`GET /api/integrations/<app>/spec`), so all of
YouTube, Instagram, Shopify, Home Assistant … and every app added later work on the phone without a new release: connect (browser sign-in, details, or one tap),
a tab per list with drill-down, a **Do something** tab, and a **Manage** sheet (full access, dashboard tile, settings, disconnect), exactly like the web app.
GitHub, Notion, Discord and Spotify show their dashboard summary and Manage sheet here (their detailed screens are on the web).

Signing in to an app opens your browser; approve there, return to the app, and it picks the connection up on its own.
Set `--dart-define=WEB_URL=https://your-web-app` so links to web-only pages open your Daksha.
