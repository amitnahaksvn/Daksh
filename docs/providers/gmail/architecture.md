# Architecture

```
Browser ──► Next.js control centre ──► Daksh.Api  /api/integrations/gmail/*
                                         │  GmailGuard (account → enabled → permission → OAuth scope)
                                         ▼
                              GmailServices ──► Gmail REST API (token refresh, retry/backoff+jitter)
                                         │
Google Pub/Sub ──push──► /push ──flag──► gmailSyncState.SyncRequested
                                                  │
GmailSyncWorker (60 s tick) ◄────────────────────┘  also: interval/reconcile, watch renewal, retention purge
        │
        ▼
GmailSyncEngine  full sync (profile.historyId first) / incremental (history.list) / full-resync on 404
        │ per message, bounded concurrency 4, failures isolated (dead letters)
        ▼
GmailPipeline: exclusions → classify → persist → notifications → automation events → follow-up tracking → automatic rules
GmailPipeline.EvaluateRepliesAsync / SweepFollowUpsAsync (after each sync)
```

Key types (all under `Modules/Gmail/`):
- `GmailCore.cs` – scopes, permission profiles, per-account config + validation, `GmailGuard`, backoff, local-data purge.
- `GmailIntelligence.cs` – classifier, deadline/action extraction, prompt-injection guard.
- `GmailRules.cs` – rule model + engine, search-query builder.
- `GmailSupport.cs` – attachment safety, reply policy/composer, follow-up planner.
- `GmailModels.cs` – stored documents, Gmail message parser.
- `GmailServices.cs`, `GmailPipeline.cs`, `GmailSync.cs` – service layer, pipeline, sync engine + worker.
- `GmailManagement*.cs` – HTTP endpoints. Endpoints hold no Gmail logic beyond calling services.

Conventions followed: MongoDB (the repo's database), minimal-API modules, `ISecretProtector` for tokens, `integrationConnections` for the
OAuth connection (one per Gmail account), singletons registered in `AddGmailIntegration`.

Multi-account: each connected address is its own `IntegrationConnection`. `gmailAccounts` adds nickname/default/enabled/granted scopes;
`gmailAccountConfigs` holds independent settings per account. Requests take `?accountId=` and default to the default enabled account.
