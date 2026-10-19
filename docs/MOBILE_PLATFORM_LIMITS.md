# Mobile platform restrictions (honest summary)

- **Background microphone**: iOS and Android do not allow an app to listen continuously in the background. Android requires a
  visible foreground service with a persistent notification and a `microphone` foreground-service type (Android 14+ also restricts
  starting it from the background); iOS permits background audio only in narrow cases (active call/recording session, visible
  indicator) and App Review scrutinizes always-listening apps. Plan: push-to-talk and foreground wake-word only; no covert recording.
- **Cellular call control**: iOS gives third-party apps no API to answer, place, record or screen carrier calls (CallKit is for the app's
  own VoIP calls). Android's `InCallService`/call-screening roles require the user to set the app as the default dialer/screening app
  and are Play-policy-restricted; call recording is restricted and legally regulated. Treat as DEFERRED_WITH_REASON.
- **Third-party messaging** (WhatsApp, iMessage, SMS): no general API to read or send on the user's behalf. Only official business
  APIs (e.g. WhatsApp Business) or user-initiated share/intents are possible. Android SMS access is Play-restricted.
- **Background execution**: tasks are time-limited and OS-scheduled; reliable reminders must come from server push (FCM/APNs) or local
  scheduled notifications, not a long-running process.
- **Permissions**: request at point of use, explain why, work without them, and let users revoke in the Privacy screen and OS settings.
