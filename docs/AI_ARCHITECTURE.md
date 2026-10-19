# AI architecture (LLM, RAG, ML)

Principles: **provider-agnostic**, **privacy-by-policy**, **honest about simulation**, **observable**, **testable offline**.

## Layers (`services/ai/Daksh.Ai`)

| Concern | Abstraction | Today | Production target |
|---------|-------------|-------|-------------------|
| LLM access | `Microsoft.Extensions.AI.IChatClient` | `MockChatClient` | Anthropic / OpenAI / Azure OpenAI / Ollama adapters (all offer `IChatClient`); wrap with M.E.AI middleware for retry, caching, OpenTelemetry |
| Model choice | `IModelRouter` (`PolicyModelRouter`) | Local-vs-cloud privacy policy, prefers real over mock, then cost rank | Add task/latency/budget routing, fallback chains |
| Prompts | `IPromptRegistry` (versioned) | In-code v1 system prompt, version logged per turn | Prompt files + offline eval gate in CI |
| Guardrails | `ITextGuardrail` | PII redaction for cloud-bound input; prompt-injection flagging | Output moderation, tool-call policy, jailbreak classifier |
| Memory / RAG | `IMemoryStore` + `IEmbeddingGenerator` | In-process cosine over mock embeddings | PostgreSQL + pgvector adapter (image already pgvector-enabled); chunking, source attribution |
| Tools / agents | `IAgentTool`, `AgentToolRegistry` | Registry; side-effect tools refuse to run without explicit user confirmation | MCP client adapters, per-tool scopes, multi-step agent loop in Worker |
| Classical ML | `IMlModel<TIn,TOut>` | `HeuristicEmailPriorityModel` (declares `IsHeuristicPlaceholder`) | ONNX Runtime / ML.NET models, versioned artifacts, offline eval |
| Speech | `ISpeechRecognizer`, `ISpeechSynthesizer` (Integrations) | Mock | Cloud/on-device engines |
| Telemetry | `AiTelemetry` ActivitySource + Meter | Requests/tokens per model | OTel exporter, cost dashboards |

## Turn pipeline (`AssistantOrchestrator`)

1. Router selects a model from the user's `UserAiContext`. **Cloud models are ineligible unless the user enabled provider data sharing**
   (default off); if none is eligible the API returns 409 rather than silently leaking data.
2. Input guardrails run with knowledge of the target locality (PII is redacted only when leaving for a cloud model).
3. If the user enabled memory, top-k memories are retrieved and injected as *data, not instructions*.
4. Versioned system prompt + history + message -> `IChatClient`.
5. Output guardrails, usage metrics, structured log (model, prompt@version, tokens, flags).
6. API persists messages and an audit event (model, tokens, guardrail flags).

## Rules that must hold

- No assistant output claims an action was performed unless a tool actually ran and succeeded.
- Content from email/web/documents is untrusted data; tools with side effects require user confirmation.
- Memory is opt-in, per-user isolated and deletable (`IMemoryStore.DeleteAllAsync`).
- Mock/heuristic components are labelled in API responses (`isSimulated`) and `GET /api/v1/integrations`.

## Evaluation (planned, REQ-AI-006)

Golden-set regression for prompts and routing, retrieval quality (recall@k), guardrail precision/recall, and ML model
metrics gated in CI before a prompt or model version is promoted.
