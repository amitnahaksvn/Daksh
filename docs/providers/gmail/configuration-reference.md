# Configuration reference

Stored per account in `gmailAccountConfigs` (camelCase over the API) and edited in the control centre. `PUT /config` validates and clamps every
field and uses an integer `version` for optimistic concurrency (`409 version_conflict`). Defaults are safe: approval required, no auto-send.

**Sync** – `enabled`, `intervalMinutes` (5–1440, used when push is unavailable), `nearRealTime`, `initialPeriodDays` (0/7/30/90/180; 0 = all history),
`labels`, `includeSpam`, `includeTrash`, `excludedSenders|Domains|SubjectKeywords|Labels`. Excluded mail is skipped entirely.

**AI** – `enabled`, `scope` (all / important / categories + `categories`), `summaryLength`, `excludedSenders|Domains|Labels`,
and per feature (25 ids) `enabled` + `confidenceThreshold`. Features: summarisation, longThreadSummary, important, urgent, actionItems, deadlines,
meetingRequest, invoice, receipt, paymentReminder, recruitment, travel, flightHotel, newsletter, promotional, spamSuspicious, sentiment, complaint,
duplicate, followUp, decisions, contacts, orderDelivery, subscription, legal.

**Reply** – `suggestionsEnabled`, `mode` (AskFirst · DraftOnly · AutoRules · AutoCategories), `autoSend` (forced off in the first two modes),
`autoSendCategories`, new/existing thread switches, sender/domain/label allow/deny lists, exclusion switches (automated, OTP/security, financial, legal,
suspicious), business hours/weekends/holidays, `replyDelayMinutes`, `maxPerHour/Day`, `minConfidence`, `style`, `greeting`, `closing`, `signature`, `maxLength`,
`phrasesToAvoid`, `neverCommitTo`.

**Follow-up** – `enabled`, `intervalsDays`, `notifyBeforeDeadline`, `deadlineLeadHours`.

**Notifications** – per-type switches (16 types), `quietHoursStart/End`, `maxPerHour`.

**Attachments** – `enabled`, `allowedExtensions`, `excludedExtensions`, `maxSizeMb`, `autoClassify`, `detectDuplicates`, `retentionDays`.

**Permissions** – `profile` (ReadOnly · Assisted · Autonomous) and per-permission `overrides`. **Privacy** – `storeSnippets`, `retentionDays`.

Settings present in the stored document but **intentionally not exposed or without effect yet** (no backing feature): `ai.language`, `ai.modelId`,
`ai.allowExternalProviders`, per-feature `priority`, `reply.personalInstructions/businessInstructions/language`, `followUp.autoSend` (always forced off),
`notifications.digest/group`, `attachments.maxTotalStorageMb/aiAnalysis/deleteProcessedCopies`, `sync.syncAttachments/maxAttachmentMb`. They exist so
saved data stays compatible when those features arrive; the UI hides them.

Enforced server-side: retention (worker purges synced message info and attachment records past their retention days), exclusions, thresholds,
quiet hours and hourly caps (notifications are recorded but marked suppressed), reply limits, and the permission profile on every endpoint.
