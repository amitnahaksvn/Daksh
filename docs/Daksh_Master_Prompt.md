# MASTER PROMPT: BUILD A COMPLETE OPENCLAW-INSPIRED SELF-HOSTED AI AGENT PLATFORM

## 1. YOUR ROLE

Act as a principal software architect, senior .NET engineer, senior React engineer, AI/LLM engineer, DevOps engineer, security engineer, QA automation engineer, and technical writer.

Your mission is to design, implement, test, document, and deliver a production-grade, self-hosted, extensible AI assistant and agent platform inspired by the capabilities of OpenClaw.

This is an end-to-end engineering project, not a prototype, proof of concept, demo, or basic chatbot.

The goal is to build a platform with comparable core capabilities and extensibility to OpenClaw, using our own implementation, architecture, branding, and code.

Do not copy proprietary assets, trademarks, or code. Respect the licenses of all third-party dependencies and reference projects.

### Non-negotiable requirements

1. Do not stop after architecture, planning, scaffolding, or a basic implementation.
2. Do not implement only the happy path.
3. Do not silently omit a feature because it is complex.
4. Do not claim a feature is complete unless its implementation and tests prove it.
5. Do not leave placeholder methods, fake integrations, hardcoded responses, or TODO implementations in features marked complete.
6. Do not stop when a build fails. Investigate, fix, and run it again.
7. Do not ask me for approval after every implementation phase. Make reasonable engineering decisions and continue.
8. Ask me only when a decision requires a real external credential, irreversible action, paid service, or essential product decision that cannot safely be assumed.
9. Use mock implementations and test adapters when external credentials are unavailable, but clearly identify the real integration as not yet verified.
10. Preserve existing working code when modifying an existing repository.
11. Never delete existing user data, configuration, or functionality without explicit authorization.
12. Keep documentation and implementation status updated throughout the project.

The final result must be runnable, maintainable, secure, extensible, observable, and testable.

---

# 2. TECHNOLOGY STACK

Use the following technology choices unless the existing repository has a strong technical reason to retain an alternative.

## Backend

- .NET 10
- ASP.NET Core Web API
- C#
- Entity Framework Core
- PostgreSQL
- pgvector for vector search
- Redis for caching, distributed coordination, and ephemeral state
- RabbitMQ for asynchronous messaging where needed
- SignalR for real-time communication
- Hangfire or an equivalent durable scheduling mechanism
- OpenTelemetry
- Serilog
- FluentValidation
- Swagger / OpenAPI
- xUnit
- NSubstitute
- Testcontainers
- Playwright for end-to-end testing

## Frontend

- React
- TypeScript
- Vite
- Tailwind CSS
- A consistent component system
- React Query
- Zustand or an equivalent state management library
- SignalR client
- React Hook Form
- Zod
- Playwright

## Infrastructure

- Docker
- Docker Compose
- PostgreSQL
- Redis
- RabbitMQ
- pgvector
- Persistent volumes
- Health checks
- Structured logs
- OpenTelemetry-compatible monitoring
- CI/CD pipeline

## Architecture

Prefer a modular monolith initially, with clearly defined module boundaries and the ability to extract services later.

Do not introduce microservices unnecessarily.

The application must support both:

- Local single-user deployment.
- Shared multi-user/team deployment.

The architecture must support Windows, Linux, Docker, and cloud/VPS hosting wherever technically applicable.

---

# 3. FIRST TASK: REVERSE-ENGINEER AND AUDIT THE REFERENCE PRODUCT

Before writing implementation code, investigate the current OpenClaw product.

Official references:

- https://openclaw.ai/
- https://docs.openclaw.ai/
- https://docs.openclaw.ai/concepts/features
- https://docs.openclaw.ai/tools
- https://github.com/openclaw/openclaw

Do not rely exclusively on a marketing overview or an old feature list.

Inspect the current documentation, architecture, configuration references, feature guides, plugin ecosystem, release notes, and repository where useful.

Create:

- `docs/REFERENCE_PRODUCT_ANALYSIS.md`
- `docs/FEATURE_PARITY_MATRIX.md`
- `docs/ARCHITECTURE.md`
- `docs/IMPLEMENTATION_ROADMAP.md`
- `docs/TECHNICAL_DECISIONS.md`
- `IMPLEMENTATION_STATUS.md`

The feature parity matrix must contain:

| Field | Description |
|---|---|
| Feature ID | Unique identifier |
| Feature | Name of the feature |
| Reference capability | What the reference product supports |
| Our implementation | How we will implement it |
| Priority | Critical, high, medium, or low |
| Dependencies | Required modules or services |
| Status | Not started, in progress, implemented, tested, blocked, or excluded |
| Evidence | Code location, tests, documentation |
| Limitations | Known differences or external dependencies |

Audit every relevant capability.

Do not silently classify a difficult feature as out of scope.

Every feature must be classified as:

- Implemented and verified.
- Implemented but not fully verified.
- Blocked by an external dependency or credential.
- Intentionally excluded with a documented reason.
- Not implemented.

The audit must include newly discovered capabilities that are not explicitly listed in this prompt.

The reference product changes over time. Revisit its official documentation during the project and update the matrix if new relevant capabilities are discovered.

---

# 4. CORE ARCHITECTURE

Build the following logical components.

## 4.1 Gateway

The Gateway is the central control plane.

Responsibilities:

- Receive messages from all connected channels.
- Authenticate incoming requests.
- Resolve users, workspaces, agents, and sessions.
- Route messages to the correct agent.
- Manage active agent executions.
- Coordinate tool execution.
- Stream responses.
- Publish real-time events.
- Manage channel connections.
- Coordinate automation and scheduled jobs.
- Manage paired devices and nodes.
- Maintain session state.
- Handle reconnects and failures.
- Expose secure APIs and real-time interfaces.
- Provide operational status and diagnostics.

Support:

- Gateway startup and graceful shutdown.
- Health and readiness checks.
- Connection lifecycle management.
- Configurable host and port.
- Secure local and remote access.
- Authentication and authorization.
- Connection limits and rate limits.
- Reconnection and recovery.
- Gateway configuration validation.
- Gateway version and compatibility reporting.

## 4.2 Agent Runtime

Implement a complete agent execution engine.

An agent must be able to:

1. Receive a user request.
2. Understand the request.
3. Load its instructions and identity.
4. Load conversation history.
5. Retrieve relevant memory.
6. Select an appropriate model.
7. Determine whether tools are required.
8. Plan the execution.
9. Invoke tools.
10. Observe tool results.
11. Continue reasoning and execution.
12. Handle errors and retries.
13. Produce a final response.
14. Persist conversation and execution state.
15. Report progress to the user.

Support:

- Streaming model responses.
- Streaming tool execution events.
- Tool-call loops.
- Multiple tool calls in one execution.
- Sequential and parallel tool calls.
- Cancellation.
- Interruption.
- Timeouts.
- Retry policies.
- Execution checkpoints.
- Resumable executions.
- Durable execution history.
- Context window management.
- Context compaction and summarization.
- Token tracking.
- Cost tracking.
- Model fallback.
- Per-agent execution limits.
- Per-user execution limits.
- Queue modes.
- Per-session execution serialization.
- Concurrency control.
- Execution tracing.
- Failure recovery.

Never lose an execution silently.

## 4.3 Agent Identity and Instructions

Each agent must support:

- Name.
- Description.
- Avatar.
- System instructions.
- Personality.
- Default model.
- Fallback models.
- Tool permissions.
- Skill assignments.
- Memory configuration.
- Workspace.
- Execution limits.
- Channel routing.
- Session configuration.
- Security policies.
- Enabled/disabled state.

Support reusable instruction files and workspace-level configuration.

Provide an editable UI for agent configuration.

---

# 5. MULTI-AGENT SYSTEM

Build a proper multi-agent architecture.

Features:

- Create multiple agents.
- Assign different responsibilities.
- Give each agent its own workspace.
- Give each agent its own memory.
- Give each agent its own model configuration.
- Give each agent its own tools and permissions.
- Route users and channels to specific agents.
- Support agent-to-agent communication.
- Support delegation.
- Support sub-agents.
- Support parallel agent execution.
- Support agent result aggregation.
- Support parent-child execution relationships.
- Support agent execution monitoring.
- Support agent cancellation.
- Support delegation depth limits.
- Prevent infinite agent delegation loops.
- Track agent-to-agent token usage and cost.
- Provide isolated sessions.
- Provide shared sessions when explicitly configured.
- Define which agents can communicate with each other.
- Define approval requirements for delegated actions.

Support multiple model providers and model configurations across agents.

---

# 6. MODEL PROVIDERS AND LLM ABSTRACTION

Create a provider-independent model abstraction.

Do not tightly couple the agent runtime to one LLM provider.

Implement provider adapters for:

- OpenAI.
- Anthropic Claude.
- Ollama.
- OpenAI-compatible endpoints.
- Anthropic-compatible endpoints.

Design extensibility for:

- Google Gemini.
- Azure OpenAI.
- AWS Bedrock.
- OpenRouter.
- Groq.
- Mistral.
- Local inference servers.
- vLLM.
- llama.cpp.
- LM Studio.
- Other providers.

Support:

- API key authentication.
- OAuth authentication where supported.
- Custom endpoints.
- Local models.
- Model discovery where supported.
- Model selection.
- Model capabilities.
- Tool calling.
- Streaming.
- Vision.
- Structured outputs.
- Embeddings.
- Audio capabilities where supported.
- Provider-specific request options.
- Provider health checks.
- Rate-limit handling.
- Retry and fallback.
- Token usage.
- Cost calculation.
- Per-agent provider configuration.
- Provider-level logging without exposing secrets.

Implement a provider registry and adapter interface.

The platform must continue working when one provider is unavailable if a configured fallback exists.

Do not pretend all models support all capabilities. Track model capability metadata and validate requests before execution.

---

# 7. CHANNEL AND MESSAGING INTEGRATIONS

Create a channel abstraction that allows new channels to be added without changing the agent runtime.

Implement the channel framework first.

Support these integration targets:

- WebChat.
- Telegram.
- Discord.
- Slack.
- WhatsApp.
- Microsoft Teams.
- Google Chat.
- Signal.
- iMessage where platform restrictions permit.
- Matrix.
- IRC.
- Mattermost.
- Feishu.
- LINE.
- SMS.
- Voice Call.
- Other channels through plugins.

Do not falsely mark an integration as supported if the actual API integration is incomplete.

For each channel, support where the platform API permits:

- Connection and configuration.
- Authentication.
- Webhooks.
- Message receiving.
- Message sending.
- Message editing.
- Message deletion.
- Reactions.
- Threads.
- Replies.
- Group conversations.
- Direct messages.
- Mentions.
- Attachments.
- Images.
- Documents.
- Audio.
- Video.
- Typing indicators.
- Delivery status.
- Error handling.
- Reconnection.
- Rate limits.
- Channel-specific formatting.
- User identity mapping.
- Message history synchronization where supported.

Support:

- Multiple accounts for a channel.
- Channel-to-agent routing.
- User allowlists.
- Group allowlists.
- Mention-based activation.
- Direct message pairing.
- Sender authorization.
- Group isolation.
- Channel-level policies.
- Per-channel configuration.

Create a common channel interface and separate adapters.

Provide mock channel adapters for testing.

---

# 8. SESSION AND CONVERSATION MANAGEMENT

Implement durable session management.

Features:

- Create sessions.
- Rename sessions.
- Archive sessions.
- Delete sessions.
- Search sessions.
- Session history.
- Session metadata.
- Session ownership.
- Session participants.
- Session sharing.
- Session isolation.
- Group sessions.
- Direct message sessions.
- Agent-specific sessions.
- Channel-specific sessions.
- Session export.
- Session import.
- Session recovery.
- Session continuation.
- Session reset.
- Session branching where appropriate.
- Context summarization.
- Message pagination.
- Message attachments.
- Message editing where appropriate.
- Execution history.
- Tool-call history.

Support concurrent users without mixing their conversations or permissions.

Maintain an immutable audit trail for security-sensitive actions.

---

# 9. TOOL FRAMEWORK

Build a secure, extensible tool execution framework.

A tool must have:

- Unique name.
- Description.
- Input schema.
- Output schema.
- Version.
- Required permissions.
- Execution timeout.
- Retry policy.
- Risk classification.
- Audit configuration.
- Availability status.

Implement tool discovery and registration.

The agent must see only the tools allowed by its active policy and context.

## 9.1 Core tools

Implement:

- File read.
- File write.
- File edit.
- File search.
- Directory listing.
- File upload/download.
- Shell execution.
- Process management.
- Browser automation.
- Web search.
- Web page fetching.
- HTTP requests.
- API calls.
- Database access through controlled adapters.
- Memory search.
- Memory write.
- Messaging.
- Session management.
- Agent delegation.
- Scheduling.
- Webhook management.
- Device/node commands.
- Image analysis.
- Media generation adapters.
- Code execution where safely supported.

## 9.2 Tool execution safety

Implement:

- Tool permission policies.
- Allowlist and denylist.
- Tool-specific permissions.
- Per-agent restrictions.
- Per-user restrictions.
- Sandboxed execution.
- Working-directory restrictions.
- Command validation.
- File access restrictions.
- Network access restrictions.
- Execution timeouts.
- Resource limits.
- Audit logs.
- Approval requirements.
- Output-size limits.
- Secret redaction.

Do not run arbitrary shell commands in the main application process.

Use isolated workers or containers for risky execution.

Support configurable approval policies for high-risk actions.

---

# 10. BROWSER AUTOMATION

Build a dedicated browser automation subsystem.

Use Playwright or an equivalent supported browser automation technology.

Features:

- Browser launch and shutdown.
- Multiple browser sessions.
- Persistent browser profiles.
- Isolated browser profiles.
- Cookie management.
- Authentication sessions.
- Navigation.
- Clicking.
- Typing.
- Form filling.
- Scrolling.
- Screenshots.
- DOM inspection.
- Element selection.
- Downloads.
- Uploads.
- Tab management.
- Browser logs.
- Network diagnostics where appropriate.
- Browser session persistence.
- Browser execution timeouts.
- Browser recovery.

The agent must be able to use a browser through tools.

Support configurable browser isolation and separate user profiles.

Protect against malicious websites, prompt injection, and unintended downloads or actions.

---

# 11. MEMORY SYSTEM

Implement a persistent memory system.

Memory must be more than conversation history.

Support:

- Short-term memory.
- Long-term memory.
- User memory.
- Agent memory.
- Workspace memory.
- Session memory.
- Shared memory with explicit permissions.
- Structured memory.
- Document memory.
- Semantic memory.
- Episodic memory.

Implement:

- PostgreSQL storage.
- pgvector embeddings.
- Embedding provider abstraction.
- Semantic search.
- Keyword search.
- Hybrid search.
- Metadata filtering.
- Relevance ranking.
- Memory extraction.
- Automatic memory creation.
- Memory updates.
- Memory consolidation.
- Duplicate detection.
- Memory expiration policies.
- Memory deletion.
- User-controlled memory.
- Memory citations/provenance.
- Memory access permissions.
- Memory import/export.

The system should identify useful facts from conversations without blindly saving every message.

Provide controls to inspect, correct, export, and delete memories.

Support different embedding providers.

If embeddings are unavailable, provide a functional keyword-search fallback.

---

# 12. SKILLS SYSTEM

Build a skill framework.

A skill is a reusable set of instructions and procedures that teaches an agent how to perform a task.

Support:

- Skill discovery.
- Skill installation.
- Skill removal.
- Skill enable/disable.
- Skill configuration.
- Skill versioning.
- Skill dependencies.
- Skill compatibility checks.
- Skill permissions.
- Skill validation.
- Skill documentation.
- Skill execution tracking.
- Workspace-specific skills.
- Agent-specific skills.
- Shared skills.
- Skill updates.
- Skill import/export.

Support a `SKILL.md`-style instruction format.

Create a skill management UI.

Support an extensible skill marketplace or catalog.

Never execute untrusted skill code without validation and appropriate isolation.

---

# 13. PLUGIN SYSTEM

Build a proper plugin architecture.

Plugins should be able to extend:

- Channels.
- Model providers.
- Tools.
- Skills.
- Hooks.
- Web search.
- Web fetching.
- Speech.
- Real-time voice.
- Media generation.
- Automation.
- Storage.
- Integrations.
- Workflows.

Support:

- Plugin manifest.
- Plugin metadata.
- Plugin version.
- Compatibility information.
- Dependencies.
- Installation.
- Enable/disable.
- Removal.
- Updates.
- Configuration.
- Secret requirements.
- Permissions.
- Lifecycle hooks.
- Health checks.
- Logs.
- Error isolation.
- Plugin discovery.
- Plugin SDK.
- Plugin documentation.
- Plugin testing.
- Plugin packaging.

Support plugins from trusted registries, repositories, and local packages.

Create a plugin developer guide and example plugin.

Do not load arbitrary untrusted plugin code into the main process.

Use isolation appropriate to the plugin's trust level.

---

# 14. AUTOMATION, CRON, HEARTBEAT AND WEBHOOKS

Build a durable automation engine.

Support:

- One-time jobs.
- Recurring jobs.
- Cron expressions.
- Time zones.
- Heartbeat checks.
- Scheduled agent execution.
- Scheduled reports.
- Webhooks.
- Event-driven workflows.
- Conditional triggers.
- Retry policies.
- Failure notifications.
- Job history.
- Execution logs.
- Pause/resume.
- Cancellation.
- Job ownership.
- Per-agent schedules.
- Per-user schedules.
- Automation permissions.

The agent should be able to create and manage schedules through natural language, subject to user authorization.

Examples:

- Send me a daily summary at 8 AM.
- Check a website every hour.
- Notify me when a condition becomes true.
- Run a weekly report.
- Remind me about an unfinished task.
- Execute a workflow after receiving a webhook.

Ensure jobs survive application restarts.

Prevent duplicate execution using durable job state and distributed locking.

Provide a complete automation management UI.

---

# 15. VOICE AND AUDIO

Build a voice subsystem with provider abstraction.

Support:

- Speech-to-text.
- Text-to-speech.
- Voice notes.
- Audio uploads.
- Audio playback.
- Voice conversations.
- Real-time voice where provider capabilities permit.
- Voice activity detection where appropriate.
- Audio transcription.
- Configurable voices.
- Language selection.
- Provider failover.
- Audio storage policies.

Support browser-based voice interaction.

Design integration points for mobile and desktop applications.

Do not mark real-time voice as complete if only file transcription is implemented.

---

# 16. MEDIA SUPPORT

Support:

- Image input.
- Image analysis.
- Image generation.
- Audio input.
- Audio output.
- Video input.
- Video analysis.
- Video generation adapters.
- Document input.
- PDF processing.
- Office document processing.
- File attachments.
- Media previews.

Use provider adapters for generation and analysis capabilities.

Track file size, MIME type, ownership, permissions, and storage location.

Implement safe file handling, malware scanning integration points, and configurable retention policies.

---

# 17. DEVICE NODES AND COMPANION APPLICATIONS

Build a device/node architecture.

A node is a paired companion device that exposes approved capabilities to the Gateway.

Support:

- Node registration.
- Device pairing.
- Pairing approval.
- Authentication.
- Revocation.
- Device identity.
- Device health.
- Device status.
- Capability discovery.
- Secure communication.
- Device authorization.
- Device command execution.
- Device event reporting.

Design support for:

- Windows.
- macOS.
- Linux.
- iOS.
- Android.

Device capabilities may include:

- Camera.
- Microphone.
- Screen capture.
- Screen recording.
- Voice.
- Notifications.
- Location where explicitly authorized.
- Local file access.
- Browser access.
- Device commands.

Implement the node protocol and a reference desktop node first.

Design mobile applications and their APIs so native mobile clients can be added.

Never assume a device capability exists without checking its registration and permission.

---

# 18. DESKTOP AND WEB CONTROL UI

Build a polished, responsive React application.

It must not look like a basic admin template.

Provide a professional, modern interface with consistent navigation, accessibility, loading states, error handling, and responsive layouts.

## Main navigation

- Dashboard.
- Chat.
- Agents.
- Sessions.
- Models.
- Providers.
- Channels.
- Tools.
- Skills.
- Plugins.
- Memory.
- Automations.
- Devices.
- Workspaces.
- Files.
- Approvals.
- Logs.
- Usage and costs.
- Settings.
- System health.

## Dashboard

Show:

- Gateway status.
- Active agents.
- Active sessions.
- Connected channels.
- Connected devices.
- Recent executions.
- Scheduled jobs.
- Failed executions.
- Tool activity.
- Model usage.
- Token consumption.
- Estimated costs.
- System health.

## Chat interface

Implement:

- Real-time streaming.
- Markdown.
- Code blocks.
- Syntax highlighting.
- Tool execution indicators.
- Agent progress.
- Attachments.
- Image previews.
- Audio playback.
- Message history.
- Session switching.
- Stop generation.
- Retry.
- Regenerate.
- Copy.
- Feedback.
- Model selection.
- Agent selection.
- Tool activity visibility.
- Approval requests.
- Error display.

## Agent management

Allow users to create, edit, configure, test, duplicate, disable, and delete agents.

## Provider management

Allow users to configure providers, credentials, models, endpoints, fallback rules, and capabilities.

## Channel management

Provide setup guidance, connection status, account configuration, webhook information, and troubleshooting.

## Tool and plugin management

Show available tools, permissions, configuration, status, logs, and dependencies.

## Memory management

Allow users to inspect, search, edit, export, and delete memory.

## Automation management

Provide job creation, editing, history, execution status, and failure details.

## Approvals

Provide a clear approval queue with action details, risk level, requester, and approve/reject options.

## Settings

Support user preferences, workspace settings, security, authentication, model defaults, storage, logging, and system configuration.

---

# 19. MULTI-USER, WORKSPACES AND ACCESS CONTROL

Support individual and team deployments.

Implement:

- User registration.
- Login.
- Logout.
- Password reset.
- Secure password hashing.
- Session management.
- Optional MFA.
- API keys.
- Role-based access control.
- Permission-based authorization.
- Workspace creation.
- Workspace membership.
- Workspace isolation.
- User invitations.
- Role assignment.
- Ownership transfer.
- Audit logging.

Roles should include configurable equivalents of:

- Owner.
- Administrator.
- Operator.
- Developer.
- Member.
- Read-only user.

A shared Gateway must not mean unrestricted access to all users' data.

Enforce tenant and workspace isolation at the API, application, and data-access levels.

Include automated authorization tests.

---

# 20. SECURITY AND PRIVACY

Security is a core architectural requirement.

Implement:

- Secure authentication.
- Authorization.
- API key protection.
- Encrypted secrets.
- Secure credential storage.
- TLS support.
- CORS configuration.
- CSRF protection where applicable.
- Rate limiting.
- Request validation.
- Input validation.
- Output encoding.
- Audit logging.
- Security event logging.
- Secret redaction.
- Secure file storage.
- Data retention policies.
- Backup encryption.
- Session expiration.
- Device pairing.
- Network access restrictions.
- Tool sandboxing.
- Plugin isolation.
- Permission boundaries.
- Approval workflows.

Explicitly defend against:

- Prompt injection.
- Malicious attachments.
- Malicious websites.
- Data exfiltration.
- Unauthorized tool execution.
- Cross-workspace access.
- Credential leakage.
- SSRF.
- Command injection.
- Path traversal.
- Replay attacks.
- Webhook spoofing.
- Unsafe plugin installation.
- Privilege escalation.

Treat messages, files, web pages, and tool outputs as untrusted data.

Never treat instructions embedded in untrusted content as trusted system instructions.

Implement policy checks in code. Do not rely only on model instructions for security.

Document the threat model and security assumptions.

---

# 21. CONFIGURATION AND SECRETS

Provide centralized configuration management.

Support:

- Environment variables.
- Configuration files.
- Database-backed settings where appropriate.
- Encrypted credentials.
- Per-user settings.
- Per-workspace settings.
- Per-agent settings.
- Per-provider settings.
- Per-channel settings.

Never commit secrets to source control.

Provide `.env.example` with placeholders only.

Implement secret rotation and credential update flows.

Redact secrets from logs, API responses, diagnostics, and frontend displays.

---

# 22. DATA MODEL AND DATABASE

Design a normalized, migration-managed database.

Include entities for:

- Users.
- Roles.
- Permissions.
- Workspaces.
- WorkspaceMembers.
- Agents.
- AgentConfigurations.
- AgentInstructions.
- AgentDelegations.
- ModelProviders.
- Models.
- ProviderCredentials.
- Sessions.
- SessionParticipants.
- Messages.
- MessageAttachments.
- ToolDefinitions.
- ToolExecutions.
- ToolPermissions.
- Skills.
- SkillVersions.
- Plugins.
- PluginConfigurations.
- Channels.
- ChannelAccounts.
- ChannelConnections.
- ChannelMessages.
- Memories.
- MemoryEmbeddings.
- MemorySources.
- Automations.
- ScheduledJobs.
- JobExecutions.
- Webhooks.
- Devices.
- DevicePairings.
- DeviceCapabilities.
- Approvals.
- AuditLogs.
- UsageRecords.
- CostRecords.
- Files.
- ExecutionCheckpoints.
- SystemSettings.

Add appropriate:

- Primary keys.
- Foreign keys.
- Indexes.
- Unique constraints.
- Tenant boundaries.
- Soft deletion where appropriate.
- Created and updated timestamps.
- Concurrency handling.
- Data retention policies.

Use database migrations.

Provide initial seed data only where appropriate.

Do not use in-memory storage as the production persistence layer.

---

# 23. API DESIGN

Create versioned REST APIs and real-time interfaces.

Organize APIs by domain.

Include:

- Authentication APIs.
- User APIs.
- Workspace APIs.
- Agent APIs.
- Model APIs.
- Provider APIs.
- Session APIs.
- Message APIs.
- Channel APIs.
- Tool APIs.
- Skill APIs.
- Plugin APIs.
- Memory APIs.
- Automation APIs.
- Device APIs.
- Approval APIs.
- File APIs.
- Usage APIs.
- Health APIs.
- Configuration APIs.
- Administration APIs.

Use consistent:

- HTTP status codes.
- Validation responses.
- Error formats.
- Pagination.
- Filtering.
- Sorting.
- Authorization.
- API versioning.
- Correlation IDs.
- OpenAPI documentation.

Use SignalR or a suitable WebSocket protocol for:

- Chat streaming.
- Agent progress.
- Tool events.
- Session updates.
- Gateway events.
- Device events.
- Automation status.
- Approval notifications.

Document every public API.

---

# 24. OBSERVABILITY AND OPERATIONS

Implement:

- Structured logging.
- Correlation IDs.
- Distributed tracing.
- Metrics.
- Health checks.
- Readiness checks.
- Gateway diagnostics.
- Provider diagnostics.
- Channel diagnostics.
- Agent execution logs.
- Tool execution logs.
- Plugin logs.
- Automation logs.
- Error reporting.
- Usage reporting.
- Cost reporting.

Track:

- Request latency.
- Model latency.
- Time to first token.
- Token usage.
- Tool duration.
- Agent execution duration.
- Failed executions.
- Queue depth.
- Provider failures.
- Channel connection health.
- Database health.
- Redis health.
- Worker health.

Provide dashboards or documented integration with monitoring platforms.

Do not expose sensitive prompts, credentials, or private user content in telemetry by default.

---

# 25. RELIABILITY AND RECOVERY

Implement:

- Durable background execution.
- Retry policies.
- Exponential backoff.
- Idempotency.
- Distributed locking.
- Duplicate-event protection.
- Dead-letter handling.
- Execution checkpoints.
- Graceful shutdown.
- Recovery after restart.
- Provider failover.
- Channel reconnection.
- Database migration safety.
- Job recovery.
- Queue recovery.

Support backup and restore for:

- Database.
- Memory.
- Workspace files.
- Configuration.
- Plugin configuration.
- Relevant application state.

Document disaster recovery and upgrade procedures.

Test restart scenarios.

---

# 26. CLI AND DEVELOPER EXPERIENCE

Provide a CLI for common administration tasks.

Commands should cover:

- Gateway start.
- Gateway stop.
- Gateway status.
- Diagnostics.
- Configuration validation.
- User management.
- Workspace management.
- Agent management.
- Provider management.
- Channel status.
- Plugin management.
- Skill management.
- Automation management.
- Database migrations.
- Backup.
- Restore.
- Logs.

Provide:

- Developer setup instructions.
- Local development scripts.
- Docker commands.
- Environment validation.
- Troubleshooting commands.
- Example configuration.
- Example agent.
- Example skill.
- Example plugin.
- Example channel adapter.

The project must be straightforward to run on a clean developer machine.

---

# 27. TESTING STRATEGY

Testing is mandatory throughout implementation.

## Unit tests

Cover:

- Agent orchestration.
- Model selection.
- Provider adapters.
- Tool registration.
- Tool policies.
- Permission checks.
- Session management.
- Memory retrieval.
- Memory extraction.
- Automation scheduling.
- Channel routing.
- Plugin lifecycle.
- Approval workflows.
- Error handling.

## Integration tests

Cover:

- API endpoints.
- PostgreSQL persistence.
- pgvector.
- Redis.
- RabbitMQ.
- SignalR.
- Background workers.
- Provider adapters.
- Channel adapters.
- Tool execution.
- Plugin loading.
- Automation persistence.

Use Testcontainers when appropriate.

## Security tests

Cover:

- Unauthorized access.
- Cross-user access.
- Cross-workspace access.
- Invalid credentials.
- Permission escalation.
- Malicious file paths.
- Command injection.
- Prompt injection handling.
- Secret exposure.
- Unsafe tool execution.
- Webhook authentication.

## End-to-end tests

Cover:

- User registration and login.
- Creating an agent.
- Configuring a model.
- Starting a conversation.
- Streaming a response.
- Executing a tool.
- Saving memory.
- Searching memory.
- Creating an automation.
- Receiving an approval request.
- Connecting a channel using a test adapter.
- Restarting the application and recovering state.

## Coverage

Set a target of at least 90% line coverage for critical business logic and application services.

Do not inflate coverage with meaningless tests.

Track branch coverage as well.

## Required end-to-end scenario

Build and verify this complete scenario:

1. A user logs into the platform.
2. The user creates an AI research agent.
3. The user configures an LLM provider.
4. The user sends a request to research a technical topic.
5. The agent searches the web.
6. The agent retrieves and processes information.
7. The agent produces a summary.
8. The agent stores relevant information in memory.
9. The user asks a follow-up question.
10. The agent retrieves the stored memory.
11. The user asks the agent to schedule a future task.
12. The scheduler persists the job.
13. The application restarts.
14. The job remains available and executes correctly when due.
15. The user receives the result through the configured channel.
16. Logs, execution history, usage, and cost records are available.

Use deterministic test adapters for automated testing. Also provide a real-provider verification procedure.

---

# 28. DEPLOYMENT

Provide:

- Dockerfiles.
- Docker Compose.
- PostgreSQL configuration.
- Redis configuration.
- RabbitMQ configuration.
- Persistent volumes.
- Environment configuration.
- Health checks.
- Database migrations.
- Reverse proxy guidance.
- HTTPS guidance.
- Backup procedures.
- Restore procedures.
- Upgrade procedures.

Support:

1. Local development.
2. Local Docker deployment.
3. Self-hosted Linux deployment.
4. VPS deployment.
5. Cloud deployment.

Provide separate development, testing, staging, and production configurations.

Do not hardcode production credentials or endpoints.

---

# 29. DOCUMENTATION

Maintain complete documentation:

- README.md
- docs/REFERENCE_PRODUCT_ANALYSIS.md
- docs/FEATURE_PARITY_MATRIX.md
- docs/ARCHITECTURE.md
- docs/IMPLEMENTATION_ROADMAP.md
- docs/TECHNICAL_DECISIONS.md
- docs/DATABASE_DESIGN.md
- docs/API_REFERENCE.md
- docs/AGENT_RUNTIME.md
- docs/MODEL_PROVIDERS.md
- docs/CHANNEL_INTEGRATIONS.md
- docs/TOOLS.md
- docs/MEMORY.md
- docs/SKILLS.md
- docs/PLUGINS.md
- docs/AUTOMATION.md
- docs/VOICE_AND_MEDIA.md
- docs/DEVICE_NODES.md
- docs/SECURITY.md
- docs/DEPLOYMENT.md
- docs/BACKUP_AND_RESTORE.md
- docs/TESTING.md
- docs/TROUBLESHOOTING.md
- docs/PLUGIN_DEVELOPMENT.md
- docs/CONTRIBUTING.md
- IMPLEMENTATION_STATUS.md

Documentation must describe the actual implementation, not an imagined future state.

---

# 30. SUGGESTED SOLUTION STRUCTURE

Use a clean and maintainable solution structure.

Example:

src/
  Backend/
    Api/
    Gateway/
    Application/
    Domain/
    Infrastructure/
    Shared/
    Modules/
      Identity/
      Workspaces/
      Agents/
      AgentRuntime/
      Models/
      Sessions/
      Channels/
      Tools/
      Browser/
      Memory/
      Skills/
      Plugins/
      Automation/
      Devices/
      Approvals/
      Files/
      Usage/
      Observability/
    Workers/
    Cli/

  Frontend/
    web/

  Node/
    Desktop/
    Mobile/

plugins/
  Providers/
  Channels/
  Tools/
  Skills/
  Examples/

tests/
  Unit/
  Integration/
  Contract/
  Security/
  EndToEnd/

deploy/
  docker/
  compose/
  scripts/
  monitoring/

docs/

Adapt this structure if the existing repository has a better established organization.

Keep domain logic independent of infrastructure wherever practical.

---

# 31. IMPLEMENTATION EXECUTION PLAN

Implement in phases, but do not stop after a phase.

## Phase 0: Repository and reference audit

- Inspect the complete repository.
- Inspect the current OpenClaw reference.
- Create the feature parity matrix.
- Identify technical risks.
- Identify missing requirements.
- Decide architecture.
- Create the implementation roadmap.

## Phase 1: Foundation

- Solution structure.
- Backend startup.
- Frontend startup.
- Database.
- Migrations.
- Redis.
- Logging.
- Configuration.
- Health checks.
- Docker Compose.
- CI pipeline.

## Phase 2: Identity and workspace

- Authentication.
- Authorization.
- Users.
- Roles.
- Workspaces.
- Tenant isolation.

## Phase 3: Model providers

- Provider abstraction.
- OpenAI.
- Claude.
- Ollama.
- OpenAI-compatible endpoints.
- Model registry.
- Streaming.
- Fallback.
- Usage tracking.

## Phase 4: Gateway and agent runtime

- Gateway.
- Agent lifecycle.
- Sessions.
- Conversation history.
- Streaming.
- Tool loop.
- Cancellation.
- Durable execution.
- Context management.

## Phase 5: Tools and browser

- Tool registry.
- Permissions.
- File tools.
- Shell execution.
- Web search.
- Web fetch.
- Browser automation.
- Sandboxing.
- Approvals.

## Phase 6: Memory

- Memory storage.
- Embeddings.
- Semantic search.
- Hybrid retrieval.
- Extraction.
- Consolidation.
- Memory UI.

## Phase 7: Multi-agent

- Agent management.
- Delegation.
- Sub-agents.
- Parallel execution.
- Isolated workspaces.
- Agent communication.

## Phase 8: Channels

- WebChat.
- Telegram.
- Discord.
- Slack.
- WhatsApp.
- Additional channel adapters.
- Pairing.
- Group routing.
- Attachments.

## Phase 9: Skills and plugins

- Skill management.
- Skill discovery.
- Plugin SDK.
- Plugin lifecycle.
- Example plugins.
- Permissions.
- Compatibility checks.

## Phase 10: Automation

- Scheduler.
- Cron.
- Heartbeat.
- Webhooks.
- Event-driven workflows.
- Durable jobs.
- Execution history.

## Phase 11: Voice and media

- STT.
- TTS.
- Audio.
- Image processing.
- Video processing.
- Generation adapters.

## Phase 12: Devices and nodes

- Pairing.
- Device registration.
- Capability discovery.
- Device authorization.
- Desktop reference node.
- Mobile integration contracts.

## Phase 13: Complete Control UI

- Dashboard.
- Chat.
- Agents.
- Sessions.
- Providers.
- Channels.
- Tools.
- Skills.
- Plugins.
- Memory.
- Automation.
- Devices.
- Approvals.
- Settings.
- Logs.
- Usage.

## Phase 14: Hardening

- Security.
- Performance.
- Reliability.
- Recovery.
- Backup.
- Restore.
- Upgrade.
- Observability.

## Phase 15: Final verification

- Run all builds.
- Run all unit tests.
- Run all integration tests.
- Run security tests.
- Run E2E tests.
- Verify Docker deployment.
- Verify restart recovery.
- Verify documentation.
- Re-audit the feature parity matrix.
- Fix all critical issues.

A phase is complete only when its acceptance criteria pass.

---

# 32. AUTONOMOUS EXECUTION AND CONTINUITY RULES

This project may take multiple sessions.

You must maintain durable progress.

At the beginning of every work session:

1. Read `IMPLEMENTATION_STATUS.md`.
2. Read `docs/IMPLEMENTATION_ROADMAP.md`.
3. Read `docs/FEATURE_PARITY_MATRIX.md`.
4. Inspect Git status.
5. Inspect the current solution state.
6. Run relevant tests.
7. Identify the next incomplete task.
8. Continue implementation.

After every meaningful task:

1. Update implementation status.
2. Update the feature parity matrix.
3. Record files changed.
4. Record tests executed.
5. Record failures.
6. Record remaining work.
7. Record external dependencies.

If context is running low:

- Save progress.
- Update the status files.
- Record the exact next task.
- Record relevant commands.
- Do not claim completion.
- Make the repository resumable.

Do not repeatedly rebuild the same functionality.

Do not overwrite working modules unnecessarily.

Do not leave uncommitted temporary experiments that make the project difficult to resume.

Do not stop because a feature is difficult. Break it into smaller tasks and continue.

If an external service requires credentials:

- Build the integration adapter.
- Build configuration and credential management.
- Build mock tests.
- Document the missing credential.
- Continue with other work.

If an external API is unavailable:

- Record the limitation.
- Build the integration contract.
- Implement a test adapter.
- Continue.

If a dependency is incompatible:

- Investigate alternatives.
- Record the technical decision.
- Choose a maintainable solution.
- Continue.

Do not repeatedly ask me questions that can be resolved through sound engineering judgment.

---

# 33. FINAL ACCEPTANCE CRITERIA

The project is not complete merely because the application starts.

The final delivery must demonstrate:

- A working self-hosted Gateway.
- A working React Control UI.
- Secure user authentication.
- Workspace isolation.
- Multiple agents.
- Multiple model providers.
- Streaming conversations.
- Tool execution.
- Tool permissions.
- Browser automation.
- Persistent sessions.
- Persistent memory.
- Skills.
- Plugins.
- Scheduled automation.
- Webhooks.
- Channel framework.
- Working verified channel integrations.
- Multi-agent delegation.
- Usage and cost tracking.
- Approval workflows.
- Operational monitoring.
- Docker deployment.
- Database migrations.
- Backup and restore documentation.
- Automated test suites.
- Security test coverage.
- Developer documentation.
- Deployment documentation.
- Feature parity matrix with evidence for every feature.

Every feature must have a truthful status.

A feature that is designed but not implemented is not complete.

A feature that works only with a mock is not a verified real integration.

A feature that depends on credentials must be explicitly identified as awaiting external verification.

No critical security issue, failing build, or failing mandatory test may be hidden in the final report.

---

# 34. FINAL DELIVERY REPORT

At the end, produce a clear report containing:

1. Architecture implemented.
2. Backend modules completed.
3. Frontend modules completed.
4. Provider integrations completed.
5. Channel integrations completed.
6. Tool integrations completed.
7. Memory and agent capabilities completed.
8. Automation capabilities completed.
9. Security controls implemented.
10. Deployment instructions.
11. Test results.
12. Coverage results.
13. Known limitations.
14. External credentials still required.
15. Features intentionally excluded.
16. Features still pending.
17. Exact commands to run the application.
18. Exact commands to run tests.
19. Location of the feature parity matrix.

Include evidence for all completion claims.

Do not report 100% feature parity unless every applicable reference feature has been audited, implemented, and verified.

---

# START NOW

Begin with Phase 0.

Inspect the existing repository and current official OpenClaw documentation.

Create the reference product analysis and feature parity matrix before implementing the application.

Then implement the entire platform incrementally, continuously building, testing, fixing, documenting, and updating progress.

Do not stop at the plan.

Do not stop at scaffolding.

Do not stop at the first working chat response.

Deliver the complete, tested, documented, production-oriented platform.

Your objective is to finish the engineering work, not merely describe how it could be done.
