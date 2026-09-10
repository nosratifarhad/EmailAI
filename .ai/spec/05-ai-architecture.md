# 05 - AI architecture

**Purpose.** Talk to **any** OpenAI-compatible Chat Completions provider, configured entirely
by the operator/user, without hard-coding a vendor.

**Scope.** Effective provider settings, request construction, error classification, limits and
the AI operation surface. Readiness is 06; prompt trust boundaries are 07.

**Inputs.** `AiOptions`/per-user overrides (Base URL, model, timeout, credential source), the
provider API key, the message/thread loaded through Exchange, the requested output language and
the current-user identity.

**Outputs.** Plain-text AI content (`AiContentResult.Content`) for: summarize message, summarize
thread, suggest reply, generate reply - and a readiness/probe status (06).

**Responsibilities.** `AiService`/`AiPrompts` (business prompts), `OpenAiCompatClient`
(HTTP + classification), `AiEndpointBuilder` (URL construction),
`AiCredentialCoordinator` (key resolution + settings surface), `AiEndpoints` (HTTP surface),
`AiTextDirectionResolver` + the UI (direction of the produced text).

**Invariants.**

1. The effective request is exactly `{BaseUrl}/chat/completions`; trailing slashes are
   normalised, so `/v1/v1` or a doubled path can never be produced.
2. The configured model is sent in the body; the key only as the `Authorization: Bearer`
   header - never in a query string, body, log line or exception.
3. The AI operation body is `{"language":"Auto"|"English"|"Persian"}` (the documented name,
   case-insensitive; the numeric value 0/1/2 is still accepted). A missing language means Auto;
   an unsupported value is a **400 with an actionable message**.
4. Every operation is cancellable end to end and bounded by its own timeout.
5. Input size is bounded per message and per thread (`AiLimits`), with truncation stated in the
   prompt rather than silently dropped.
6. AI content is never persisted; AI never sends mail (the only sender is
   `POST /api/messages/{itemId}/reply`).
7. Nothing hard-codes a vendor: any OpenAI-compatible Base URL works, including company
   gateways and local servers.

**Failure modes.**

| Failure | Classification / behaviour |
| --- | --- |
| 401/403 | `Authentication` → user-facing message in 06, no exception text shown |
| 429 | `RateLimited` → "try again later" |
| Timeout / cancellation | `Timeout` → clean error, UI stays responsive |
| Connect failure / 5xx | `Unavailable` → sanitized message |
| Malformed/empty response | `InvalidResponse` → sanitized message |
| Provider without `/models` | `GET /health/ai` reports unavailable; Settings **Test connection** performs a real request instead |
| Not configured / no key | Handled by readiness (06) before any request is attempted |

**Security constraints.** The browser never learns the Base URL of a deployment-managed
provider, the key, or any header. Failure logs carry status code, endpoint path and model only -
never request/response bodies (mail is sensitive) and never the key.

**Configuration ownership.** Administrator: `AI_BASE_URL`, `AI_MODEL`, `AI_API_KEY`,
`AI_CREDENTIAL_SOURCE`, `AI_TIMEOUT_SECONDS` (or appsettings for a self-hosted instance).
Desktop user: Base URL, model and key through **Settings → AI provider** (their own Windows
account; see 13).

**Implementation.** `src/EmailAI.Domain/AI/AiOptions.cs`,
`src/EmailAI.Application/AI/AiService.cs`, `AiPrompts.cs`, `AiLimits.cs`, `AiTextDirection.cs`,
`src/EmailAI.Infrastructure/AI/OpenAiCompatClient.cs`, `AiEndpointBuilder.cs`,
`src/EmailAI.Api/Endpoints/AiEndpoints.cs`, `src/EmailAI.Api/Web/AiContracts.cs`,
`AiLanguageRequest.cs`, `src/EmailAI.Api/Configuration/AiCredentialCoordinator.cs`.

**Tests.** `AiEndpointBuilderTests`, `OpenAiCompatClientTests`, `AiClientOpenAiIndependenceTests`,
`AiServiceTests`, `AiTextDirectionTests`, `AiLanguageRequestTests`, `AiLanguageEndpointTests`,
`AiCredentialCoordinatorTests`, `AiSettingsEndpointsTests`, `AiIntegrationTests` (opt-in, real
provider).
