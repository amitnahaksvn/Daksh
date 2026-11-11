# Reference product analysis: OpenClaw

Audit date: 2026-11-11. This is our own description of what the reference product does, written from its public
documentation. No code, text or assets are copied from it. OpenClaw is MIT-licensed (OpenClaw Foundation); Daksh is an
independent implementation in a different stack (.NET + React) with its own names, protocol and data model.

## Sources read

| Source | Read | Notes |
|---|---|---|
| `docs.openclaw.ai/concepts/features` | Full | Feature list by area |
| `docs.openclaw.ai/tools`, `/gateway/config-tools`, `/gateway/config-tools/tool-policy` | Full | Tool catalogue, profiles, groups, policy layers |
| `docs.openclaw.ai/` index and `llms.txt` | Index full; `llms.txt` about 35% verbatim, the rest summarised | Used for the navigation tree |
| `concepts/architecture`, `agent-loop`, `session`, `memory`, `multi-agent`, `queue`, `model-failover` | Full | |
| `automation/cron-jobs` (+ `schedules`, `managing-jobs`, `webhooks`) | Full | `payloads` and `delivery` sub-pages not read |
| `gateway/security` (+ `tool-permissions`) | Full | |
| `tools/skills`, `tools/plugin`, `nodes` | Full | |
| GitHub repository README | Full | Latest release number and date were **not** captured |

**Not yet read** (must be read in a later audit pass, see `IMPLEMENTATION_ROADMAP.md` Phase 15): exec-approvals,
sandboxing, compaction, plugin manifest and SDK pages, node command policy, cron payload/delivery pages, hooks pages,
`built-in-tools`, `sessions-and-subagents`, the browser tool page, per-channel pages, per-provider pages, ClawHub pages,
release notes. Capabilities known only by name from the navigation tree are marked "name only" in the parity matrix.

## 1. Shape of the product

- One long-running **Gateway** process is the control plane. It listens on loopback by default (port 18789) and speaks a
  WebSocket protocol with three frame kinds (request, response, event). The first frame must be a connect/handshake frame
  carrying a token or password. Operators (CLI, web Control UI, apps) and **nodes** (companion devices) are both clients;
  nodes connect with a distinct role. Sends and agent runs require idempotency keys. Events are not replayed to
  reconnecting clients.
- Configuration is a single JSON5 file in the user's home directory, plus per-agent workspace files. State (sessions,
  transcripts) is stored per agent in SQLite. There is no built-in multi-tenant data model: it is designed for one
  operator, with newer support for several users sharing a Gateway (per-user instruction file).
- Implementation: Node.js/TypeScript pnpm monorepo (apps, extensions, packages, skills, ui), with native companion apps.

## 2. Agent runtime

- Embedded agent loop with lifecycle phases (start, finishing, end, error) and separate streams for lifecycle,
  assistant text, tool activity and compaction.
- Run timeout default is very long (48 h); a model idle timeout applies (about 2 min cloud, 5 min self-hosted).
- Per-session serialisation with **lanes** (main, subagent, cron, nested) and a concurrency cap per lane.
- **Queue modes** for messages that arrive mid-run: steer (default), followup, collect, interrupt; per-channel overrides,
  debounce, a cap on queued messages and a drop policy.
- Context **compaction** (summarising older turns) with its own event stream.
- Hooks: plugin hooks around prompt building and tool calls; file-based hooks for commands and agent bootstrap.

## 3. Agents, identity, multi-agent

- An agent has its own workspace directory, instruction files (agent instructions, persona/"soul", identity, user
  profile, boot/bootstrap, tools notes), model settings, tool policy, sandbox settings and sessions.
- **Bindings** route inbound messages to an agent by channel, account and peer; first match wins within a tier.
- Agent-to-agent messaging is opt-in with an allowlist. **Sub-agents** are spawned per task with a concurrency cap
  (default 8), a larger "swarm" cap, depth limits, and their own sandbox requirement setting.
- Goals and progress cards, "standing orders", role templates (coordinator, researcher, reviewer, writer).

## 4. Sessions

- Session keys are derived from agent + scope. Direct messages collapse into one main session by default; other scopes
  isolate per peer, per channel+peer, or per account+channel+peer. Groups are isolated per group by default.
- Reset policies (none, daily at an hour, idle minutes), maintenance (max entries, prune after N days), incognito sessions.
- Session tools let an agent list sessions, read history, search, send to another session, spawn and yield.

## 5. Models and providers

- A large provider directory (about 90 pages): Anthropic, OpenAI, Google, Bedrock, Vertex, Ollama, OpenRouter, LiteLLM,
  LM Studio, vLLM, Groq, Mistral, DeepSeek and others, plus any OpenAI-compatible or Anthropic-compatible endpoint.
- API-key auth and subscription OAuth for some providers.
- **Failover in two stages**: rotate between auth profiles of the same provider, then move down a list of fallback
  models. Cooldowns grow on repeated failures (seconds to minutes); billing failures disable a profile for longer.
- Per-provider tool policy, prompt caching notes, cost and usage reference.

## 6. Tools

Built-in tool families (names are the reference's; ours differ):

| Family | Reference tools |
|---|---|
| Runtime | exec, process, terminal, code execution |
| Files | read, write, edit, apply patch |
| Human input | ask the user a structured question; obtain a secret without the agent seeing it |
| Web | web search (about a dozen search providers), X search, web fetch |
| Browser | browser automation |
| Messaging | send/act on the current channel |
| Sessions and agents | session list/history/search/send/spawn/yield, sub-agents, agent list, goals, progress card |
| Automation | automations (cron) |
| Gateway and nodes | gateway maintenance/config, node invoke, computer use |
| Plugins | plugin lifecycle |
| Media | view image, image/music/video generation, text-to-speech, PDF |
| Memory | memory search, memory get |
| Operator UI | arrange UI panes, themes, dashboard, canvas, widgets |
| Large catalogues | tool search/describe/dispatch (experimental), MCP server tools |

**Policy layers**, applied before the model call so a filtered tool's schema is never sent:
a base **profile** (minimal, coding, messaging, full) → per-provider/per-model profile → allow/deny lists with wildcards
(deny wins) → per-sender restrictions → a deployment-owner gateway policy (exact names and actions) → an extra gate for
sandboxed sessions. Tool **groups** name sets of tools. **Elevated mode** lets approved senders escape the sandbox for
exec. **Exec approvals** gate host commands. Loop detection exists as a config key.

## 7. Memory

- Memory is plain Markdown in the agent workspace: a long-term file, dated daily notes, a user profile file, and a
  "dreams" file written by a background consolidation pass that is on by default.
- Search is hybrid (vector + keyword) over those files using a built-in SQLite index; embeddings default to a cloud
  provider and are configurable; alternative memory plugins exist (vector DB, wiki). "Active memory" recalls before a turn.

## 8. Skills and plugins

- **Skills** are folders with a `SKILL.md` (YAML front matter: name, description, plus gating metadata such as required
  binaries, environment variables, config keys, operating systems, an install recipe). Precedence runs from workspace
  skills down to bundled ones. A prompt-size limit applies; tools search and read skills on demand. A public registry
  (ClawHub) distributes skills and plugins.
- **Plugins** have a manifest and can add channels, model providers, tools, skills, hooks, speech, real-time voice,
  media generation, web search/fetch, and fill **slots** (memory backend, context engine). They can be installed from a
  registry, enabled/disabled, allow/deny-listed, inspected and reloaded. Plugins run in the Gateway process.
- MCP servers can be attached and their tools exposed through the plugin tool group.

## 9. Automation

- Jobs have schedule kinds: one-shot at a time, fixed interval, cron expression with time zone, on-exit, and stream.
  Options include stagger/exact timing, active hours, idle-only, pacing. Recurring failures back off (30 s to 60 min).
- Delivery: announce to a channel/recipient or call a webhook. Session retention for job runs is configurable; missed
  jobs run after restart unless disabled.
- **Heartbeat**: a periodic agent turn on the main session.
- **Inbound webhooks** (off by default) with a shared token: wake, run an agent turn (idempotency key, optional wait for
  completion), and mapped custom hooks. Triggers also exist for Gmail Pub/Sub, IMAP and MCP events.
- **Hooks**: event-driven scripts with their own manifest file.

## 10. Channels

- Core: WebChat, Telegram, an agent-to-agent channel, and one more internal channel.
- Official plugins: Discord, Slack, WhatsApp, Signal, iMessage, Microsoft Teams, Google Chat, Matrix, Mattermost, IRC,
  LINE, Feishu, Nextcloud Talk, Nostr, QQ, Synology Chat, Tlon, Twitch, Zalo (two variants), SMS, Voice Call.
  External: WeChat and others.
- Common behaviour: DM policy (pairing code approval, allowlist, open, disabled), group allowlists, mention-gated
  activation in groups, access groups, broadcast groups, bot-loop protection, multiple accounts per channel, threads,
  rich messages, media in/out, per-channel streaming/chunking, dead-letter inspection.

## 11. Nodes and apps

- Nodes pair with the Gateway (device identity + token, approval required) and advertise commands: camera, device info,
  notifications, system run, screen recording, location, canvas. Invocations are gated by a gateway allow/deny list and
  per-node exec approvals.
- Apps: macOS menu-bar app, iOS node, Android node, Windows and Linux companions, a terminal UI, and the web Control UI
  (chat, sessions, config, logs, approvals).

## 12. Voice and media

- Voice-note transcription, text-to-speech with several providers, real-time voice and phone calls through plugins,
  Discord voice channels, meeting notes. Image, audio, video and document input; image/video/music generation surfaces.

## 13. Security model

- Loopback by default; token/password auth; remote access through a tunnel or tailnet. A security audit command.
- Sandboxing with container backends: mode (off / non-main sessions / all), scope (per agent, per session, shared),
  workspace access (none, read-only, read-write).
- Inbound content is treated as untrusted; DM pairing prevents strangers from driving the agent.

## 14. Operations and CLI

- A broad CLI: onboarding wizard, gateway install/start/status/restart, doctor (with fix), health, logs, config
  get/set/validate, channels, pairing, devices, agents, sessions, models, memory, skills, plugins, automations, hooks,
  nodes, sandbox, approvals, secrets, security audit, backup, reset, update, MCP.

## 15. Where Daksh deliberately differs

| Topic | Reference | Daksh | Why |
|---|---|---|---|
| Tenancy | Single operator, shared Gateway is an add-on | Users, workspaces, roles enforced in API and data access | Master prompt §19 |
| State | JSON5 file + SQLite + Markdown files | PostgreSQL (+pgvector), migrations | Master prompt §2, §22 |
| Plugin isolation | In-process | Out-of-process (stdio JSON-RPC), optional container | Master prompt §13: no untrusted code in the main process |
| Shell execution | Host or container sandbox | Never in the Gateway process; child worker or container | Master prompt §9.2 |
| Event replay | Not replayed | Execution events are persisted and can be replayed | "Never lose an execution silently" |
| Memory | Markdown files + index | Rows with provenance, scopes, expiry; hybrid search | Master prompt §11 |
| Protocol | Custom WebSocket frames | REST + SignalR | Master prompt §2 |

## 16. Capabilities found that the master prompt does not list

These are tracked in the parity matrix (IDs prefixed `X-`): queue modes and debounce; session reset policies and
incognito sessions; auth-profile rotation with cooldowns; tool profiles and groups; per-sender tool restrictions;
elevated mode; "ask the user" and "secret input" tools; goals/progress cards; standing orders; active-memory recall and
background memory consolidation ("dreaming"); MCP server tools; tool search for large catalogues; code mode; loop
detection; bot-loop protection; broadcast and access groups; Gmail/IMAP/MCP event triggers; operator-UI tools (canvas,
widgets, themes); a terminal UI; onboarding wizard and doctor; security audit command; workflow pipelines; computer use;
music generation; meeting transcription.
