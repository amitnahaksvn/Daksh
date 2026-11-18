# Sync engine

- **Claiming**: one sync per account at a time (atomic state update); a run stuck "Synchronising" for 10 minutes can be taken over.
- **Initial**: records `profile.historyId` *first*, lists messages for the configured period/labels (cap 150 per run), fetches each, processes oldest first.
- **Incremental**: `history.list` from the stored id (messagesAdded, labelsAdded/Removed → refetch; messagesDeleted → delete local copy). New id stored.
- **Expired history (404)**: automatic full resync. Idempotent because messages are keyed `{account}:{messageId}`.
- **Failure isolation**: per-message errors are counted, written to `gmailDeadLetters` (attempts, dead after 5) and never stop the batch. 401/403 abort the pass once and raise one notification.
- **Retry**: 429/500/502/503/504 retried up to 3× with exponential backoff and jitter; repeated account-level failures back off 2,4,8… up to 60 min.
- **Push**: `POST /push?token=…` verifies a shared secret (constant-time), de-duplicates by Pub/Sub `messageId`, flags the account `SyncRequested`, returns 204 (also for unknown accounts so Pub/Sub doesn't redeliver forever). The worker syncs within a minute.
- **Watch**: when `Gmail:PubSub:TopicName` is set the worker calls `users/me/watch` and renews every 24 h (Gmail expires watches after 7 days). `POST /sync/watch/renew` forces it.
- **Reconciliation**: with an active watch the worker still syncs every 6 h in case a push was missed; without push it polls at the account's interval. No tight polling.
- **Retention**: each tick removes synced message info and attachment records older than the account's retention days.
- Manual: `POST /sync/run`; `start/stop` toggle `sync.enabled`.

Limits: the worker runs inside the API process (singleton hosted service). Running several API instances is safe for correctness (atomic claim) but each will tick.
