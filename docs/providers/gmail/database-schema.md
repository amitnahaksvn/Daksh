# Database schema (MongoDB)

There are no SQL migrations: collections are created on first write and `EnsureGmailManagementIndexesAsync` creates the indexes at API startup
(run once per deployment against the configured database). Documents use PascalCase field names (the repo's default mapping).

| Collection | Key | Purpose / notable indexes |
|---|---|---|
| `integrationConnections`, `integrationCredentials` | existing | One connection per Gmail account; tokens encrypted |
| `gmailAccounts` | connectionId | nickname, default, enabled, granted scopes, workspace flag · `UserId` |
| `gmailAccountConfigs` | connectionId | all settings above, `Version` |
| `gmailSyncState` | accountId | history id, status, last success/failure, watch expiry, `SyncRequested` |
| `gmailSyncHistory` | ObjectId | one row per run · `AccountId, StartedUtc desc` |
| `gmailMessages` | `{account}:{messageId}` | synced metadata + AI results (no bodies) · `User/Account/Date`, `Account/Thread`, `Account/ContentHash`, reply queue |
| `gmailRules`, `gmailRuleRuns` | ObjectId | rules; execution history · `RuleId, MessageId, Trigger` (duplicate guard) |
| `gmailFollowUps` | `{account}:{thread}` | follow-up tracking · `User/Account/Status/DueAtUtc` |
| `gmailNotifications` | `{account}:{type}:{subject}` | deduplicated by key · `User/Account/CreatedAt` |
| `gmailActionAudit` | ObjectId | audit log · `User/Account/At`, `User/Action` |
| `gmailWebhookEvents` | Pub/Sub messageId | exactly-once pushes · **TTL on `ExpiresAtUtc`** |
| `gmailAutomationEvents` | `{account}:{trigger}:{subject}` | outbox for the automation engine |
| `gmailCorrections`, `gmailSavedSearches`, `gmailAttachments`, `gmailActionItems`, `gmailReplyLog`, `gmailDeadLetters` | various | corrections, saved searches, attachment metadata (name/type/sha256, never bytes), tasks/reminders, one-auto-reply-per-thread, failed messages |

Ownership: every document carries `UserId` (and `AccountId`) and every query filters on `UserId`; accounts are resolved through
`integrationConnections` filtered by user. Optimistic concurrency: `gmailAccountConfigs.Version`, `gmailRules.Version`.
Soft deletion is not used; disconnect and `DELETE /data` hard-delete the account's local data (audit kept unless asked).
Not stored: OAuth tokens outside `integrationCredentials`, message bodies, attachment bytes. `GmailMessage` mapping is lighter than the spec's conceptual
entity list (no separate GmailThread/GmailLabel/GmailFilter/GmailPermission tables — those are read live from Gmail or live in the config document).
