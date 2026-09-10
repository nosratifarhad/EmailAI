# 06 - AI readiness

**Purpose.** State **before** an AI action whether the assistant can be used, and what to do
when it cannot - so a user never meets a raw HTTP status, a stack trace or a silent failure.

**Scope.** The readiness states, the cheap local check vs the optional probe, the user-facing
message/action pair, and the HTTP contracts of `GET /api/ai/readiness`, `GET /health/ai` and
`POST /api/settings/ai/test`.

**Inputs.** Effective AI configuration (Base URL/model), key presence in the resolved source,
and - only when a probe is requested - the provider's `GET {BaseUrl}/models`.

**Outputs.** `{ ready, status, errorCode, message, action, canOpenSettings }` (always HTTP 200)
and a header pill (`AI connected` / `AI: not configured` / `AI: auth failed` /
`AI unavailable`). The UI disables the AI buttons while not ready and offers **Open Settings**.

**Responsibilities.** `AiReadinessService` (state machine + `AiUserMessages`),
`AiEndpoints.GetReadinessAsync`, `HealthEndpoints` (`/health/ai`),
`SettingsEndpoints.TestAiConnectionAsync`, `MailClient.razor` + `HealthStatusBadge.razor`.

**Invariants.**

1. Readiness is a **state report**, not a failure: every readiness/health response is HTTP 200.
2. The default check is local and cheap (no provider request); the probe is opt-in
   (`probe=true`) and never issues a real prompt.
3. Every state maps to a message and an action a user can act on; the message never contains a
   status code, exception text or the key.
4. `not_configured` distinguishes "no provider configured" from "user-managed provider without a
   stored key yet".
5. `/health` (application liveness) stays healthy when the provider is down.
6. A failed AI **operation** is mapped through the same message table as readiness, so the user
   sees one consistent vocabulary.

**Failure modes.**

| State | Meaning / user message | Action |
| --- | --- | --- |
| `Ready` / `connected` | Assistant usable | - |
| `NotConfigured` / `not_configured` | No Base URL/model, or a user-managed provider without a key | "Open Settings" |
| `AuthenticationFailed` / `authentication_failed` | Key rejected (401/403) | Check/replace the API key |
| `RateLimited` / `rate_limited` | Provider throttling | Try again later |
| `Unavailable` / `unavailable` | Provider unreachable or failing | Check the Base URL/network |
| `TimedOut` / `timed_out` | Provider too slow | Retry / check the network |
| `InvalidConfiguration` | Base URL/model unusable | Correct in Settings |
| Credential-store failure | The secure store could not be read | Report/retry; never shows the secret |
| Unexpected failure | Sanitized generic message | Retry |

**Security constraints.** Readiness output contains presence flags and sanitized text only: no
key, no `Authorization` header, no provider internals, no deployment-managed Base URL leakage
beyond what the user themselves configured.

**Configuration ownership.** Administrator and desktop user as in 05; readiness reports the
effective combination without revealing who supplied what (except when a check is impossible
because the deployment manages the key).

**Implementation.** `src/EmailAI.Application/AI/AiReadiness.cs`, `AiReadinessService.cs`,
`src/EmailAI.Api/Endpoints/AiEndpoints.cs`, `HealthEndpoints.cs`,
`src/EmailAI.Api/Web/AiContracts.cs`, `src/EmailAI.Api/Components/Shared/HealthStatusBadge.razor`,
`src/EmailAI.Api/Components/Pages/MailClient.razor`.

**Tests.** `AiReadinessServiceTests` (every state incl. store failure and unexpected failure),
`AiReadinessEndpointTests` (HTTP contract, no key), `AiHealthEndpointTests`,
`AiSettingsEndpointsTests` (test-connection outcomes).
