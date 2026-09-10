# 13 - Secret management

**Purpose.** Keep every credential out of files that ship, out of logs and out of API responses,
while still letting a desktop user configure the app without administrator help.

**Scope.** Where each secret lives, who may read/write it, how it is migrated, and what must
never appear in plaintext or travel to the AI provider. Configuration *semantics* are 02/05.

**Inputs.** The Exchange password and the AI API key entered on the Settings page; the
deployment environment (`EXCHANGE_PASSWORD`, `AI_API_KEY`) for server/container deployments;
retired values that must not reappear.

**Outputs.** Secrets persisted per Windows user, and presence booleans in the API/health
responses.

**Responsibilities.** `ISecretStore`/`WindowsSecretStore` (CredRead/CredWrite/CredDelete),
`SecretTargets` (target names), `WindowsCredentialStore` (AI key over `EmailAI/AI`),
`UserSettingsAiStore`/`FileUserSettingsStore` (non-secret document), `AiCredentialCoordinator`
(resolution policy), `ExchangeSettingsService` (Exchange write path), `desktop/scripts/verify-secrets.js`
(release gate).

**Invariants.**

1. Secrets live **only** in the Windows Credential Manager, under their own targets:
   `EmailAI/AI` (API key) and `EmailAI/Exchange` (Exchange password). One can never be read,
   overwritten or deleted by accident when the other is managed.
2. The non-secret settings document `%APPDATA%\EmailAI\settings.json` contains no secret - only
   endpoint, mode, account, base URL, model (verified by tests).
3. Nothing secret is ever written next to the executable, into `appsettings*.json`, into the
   installer, into a log, into a health/API response or into an exception message.
4. The key/password is sent to the server exactly once (over the loopback connection) and is
   never echoed back; the UI can save, replace or remove it but never display it.
5. `AI_CREDENTIAL_SOURCE` decides the runtime policy: `auto` (secure store wins, environment
   fallback), `windows` (store only, Settings-managed) or `environment` (environment only;
   the Settings page then refuses to save/remove).
6. Switching the Exchange mode to `Windows` deletes the stored Exchange password - a mode that
   cannot use it must not keep it.
7. The Windows Credential Manager entry is scoped to the Windows user: another account on the
   same machine cannot read or remove it.
8. The AI provider never receives a credential other than the provider's own API key as its
   `Authorization: Bearer` header - never the Exchange password, never an Exchange token.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Credential store unavailable (non-Windows host, no logon session) | Typed `SecretStoreException`; `AI_CREDENTIAL_SOURCE=environment` is documented as the server alternative |
| Empty/whitespace secret submitted | Rejected with a 400 - an empty value never overwrites a stored one |
| Corrupt settings document | Quarantined once as `settings.json.corrupt`; the app continues with defaults |
| Legacy `ai-settings.json` present | Migrated once into the unified document on first read; the old file is left in place |
| Secret present in a packaged file | `verify-secrets.js` fails the release build |

**Security constraints.** Never log, print or return a secret. Failure messages carry the
target name and the Win32 error code only. Documentation and tests use placeholders exclusively;
the release pipeline scans the publish and packaged output for secret-shaped assignments and for
known retired values supplied through environment variables.

**Configuration ownership.** Desktop user: their own secrets, through Settings (per Windows
account). Administrator/deployment: environment variables for server/container hosts (with
`AI_CREDENTIAL_SOURCE=environment` making the environment authoritative for AI).

**Implementation.** `src/EmailAI.Application/Settings/ISecretStore.cs` (`SecretTargets`),
`src/EmailAI.Infrastructure/Settings/WindowsSecretStore.cs`,
`src/EmailAI.Infrastructure/AI/WindowsCredentialStore.cs`,
`src/EmailAI.Infrastructure/Settings/FileUserSettingsStore.cs`, `UserSettingsAiStore.cs`,
`src/EmailAI.Api/Configuration/AiCredentialCoordinator.cs`,
`src/EmailAI.Infrastructure/Settings/ExchangeSettingsService.cs`,
`desktop/scripts/verify-secrets.js`.

**Tests.** `AiCredentialCoordinatorTests`, `FileUserSettingsStoreTests` (no secret is ever
written), `UserSettingsAiStoreTests`, `ExchangeSettingsServiceTests` (secret reaches its own
target only; Windows mode deletes it), `ExchangeSettingsEndpointsTests` / `AiSettingsEndpointsTests`
(no endpoint returns a secret).
