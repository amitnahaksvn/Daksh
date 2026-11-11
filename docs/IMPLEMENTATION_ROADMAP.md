# Implementation roadmap: Daksh agent platform

Source of requirements: `docs/Daksh_Master_Prompt.md`. Feature-level status lives in `docs/FEATURE_PARITY_MATRIX.md`
(generated; edit `docs/parity/status.tsv`). Session-level progress lives in `IMPLEMENTATION_STATUS.md`.
The earlier personal-assistant checklist is kept in `docs/ROADMAP.md` (legacy services).

A phase is done only when its acceptance criteria pass. `[x]` done and verified, `[~]` partly done, `[ ]` not started.

| Phase | Scope | Acceptance criteria |
|---|---|---|
| 0 | Repository and reference audit | The six Phase 0 documents exist; every reference capability found has a matrix row |
| 1 | Foundation: projects, Gateway host, PostgreSQL + migrations, Redis, logging, config, health, Compose, CI | `dotnet build` clean; Gateway starts against PostgreSQL; `/health/ready` green; migration applies on an empty database |
| 2 | Identity and workspaces | Register/login/refresh/MFA/API-key tests pass; cross-workspace access tests pass for every resource |
| 3 | Model providers | Adapter tests against recorded wire formats pass for OpenAI, Anthropic, Ollama; fallback test passes; usage rows written |
| 4 | Gateway and agent runtime | Streaming run with tool loop, cancel, timeout, resume-after-restart, compaction and queue-mode tests pass |
| 5 | Tools, sandbox, browser, approvals | Policy tests; path/SSRF/command tests; exec only via sandbox; approval pause/resume test; browser tool test on a local page |
| 6 | Memory | pgvector + full-text + hybrid tests on PostgreSQL; keyword fallback test; extraction and dedupe tests |
| 7 | Multi-agent | Delegation, parallel children, depth limit, cycle rejection, cancel cascade, usage roll-up tests |
| 8 | Channels | Mock-channel end-to-end test; adapter tests with fake remote APIs for Telegram, Discord, Slack, WhatsApp; pairing and group policy tests |
| 9 | Skills and plugins | SKILL.md parser/validator tests; plugin process lifecycle test with the example plugin |
| 10 | Automation | Cron/interval/one-shot tests with time zones; no-duplicate test with two schedulers; restart test; webhook auth tests |
| 11 | Voice and media | File store tests; STT/TTS adapter tests with a fake remote API; document text extraction tests |
| 12 | Devices and nodes | Pairing/approval/revocation tests; node hub invoke test with the reference node |
| 13 | Control UI | All 20 sections reachable; lint, type-check, unit tests and build pass; Playwright smoke passes |
| 14 | Hardening | Security test suite passes; backup/restore round trip verified; rate-limit test; observability wired |
| 15 | Final verification | All suites green; Compose stack verified; restart recovery verified; required 16-step scenario passes; matrix re-audited against the reference docs not yet read |
| 16 | Convergence with legacy services | Tasks, reminders, email and calendar exposed as platform tools; `Daksh.Api` retired or reduced to a facade |

## Order of work inside the phases

1. Core entities → `PlatformDbContext` → migration → Gateway host (Phase 1).
2. Identity, tenancy filters, audit (Phase 2). Nothing else is exposed before this.
3. Providers + deterministic scripted provider (Phase 3). The scripted provider is what makes Phases 4-10 testable offline.
4. Runtime, tool framework, approvals (Phases 4-5), then memory (6), multi-agent (7), automation (10), channels (8),
   skills and plugins (9), files/voice (11), devices (12).
5. Control UI is built alongside from Phase 2 onward, section by section (13).
6. CLI, Compose, CI, docs, hardening (14-15).

## Risks

| Risk | Mitigation |
|---|---|
| Scope is far larger than one session | Durable status files; vertical slices that are each tested; truthful matrix |
| No provider or channel credentials | Adapters tested against fake remote endpoints; marked "not fully verified"; real-provider procedure in `docs/TESTING.md` |
| Sandbox strength on a developer machine | Container backend is the default for risky tools; process backend is opt-in and labelled |
| Reference product moves | Phase 15 re-audit; pages not yet read are listed in `REFERENCE_PRODUCT_ANALYSIS.md` |
| Two back ends (legacy + platform) drift | Phase 16; legacy tests stay in the solution |
