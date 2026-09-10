# EmailAI service context (authoritative behaviour)

This document is the authoritative description of the two external service
integrations. Keep it in sync whenever the behaviour changes.

## AI — configurable OpenAI-compatible provider

The application is **not** tied to OpenAI or any other company. It talks to
**any AI service that exposes an OpenAI-compatible Chat Completions API**.

```text
User
 ├── Base URL     (e.g. https://api.openai.com/v1  or  https://some-provider.example.com/v1)
 ├── API Key      (secret - never in the browser, logs, responses or diagnostics)
 └── Model        (sent verbatim in the request body)
        ↓
     EmailAI  (server-side only; the browser never calls the provider)
        ↓
 OpenAI-compatible API  (chat/completions)
        ↓
 Configured AI service  (the application never needs to know which company)
```

### Rules

* Base URL, model and key are fully configurable per deployment and, on the
  desktop, per user (Settings). Nothing is hard-coded to a vendor.
* The effective request URL is `{BaseUrl}/chat/completions` (probe:
  `{BaseUrl}/models`). Trailing slashes are normalised and `/v1/v1`,
  `//chat/completions` or a doubled `/chat/completions/chat/completions` can
  never be produced (see `AiEndpointBuilder` + `AiEndpointBuilderTests`).
* The configured model is sent in the request body; the configured key is sent
  only as the `Authorization: Bearer` header of the outbound request.
* The API key is resolved at runtime through `IAiCredentialProvider`
  (`AiCredentialCoordinator`): per-user Windows Credential Manager entry wins
  on the desktop (`EmailAI/AI`), then the configured `AI_API_KEY`. Keys are
  never cached to disk, logged, echoed by an API/health response, or put into
  exception messages.
* Failures are typed (`AiException`): authentication (401/403), rate limit
  (429), timeout, unavailable/connectivity and invalid/malformed responses are
  all classified separately.
* Every operation is given the **authoritative current user** (`MailboxIdentity`,
  resolved from the Exchange context by `IMailboxIdentityProvider` - never inferred from
  the email text). It is delivered as trusted context outside the untrusted-data fences,
  with aliases derived only from the authoritative display name / account / SMTP address,
  and it carries no credential. When no identity can be resolved the prompt says so and
  forbids guessing a name or signature.
* AI readiness is checked before an operation (`IAiReadinessService`): a cheap local check
  distinguishes "not configured", "no key yet" and "no model", and an optional probe
  (`GET {BaseUrl}/models`) distinguishes unreachable / rejected key / rate-limited /
  timeout. Every state maps to a user-facing message + action (`AiUserMessages`), so no
  HTTP status or exception text reaches the user.

### Env/config surface

| Setting | Configuration | Environment |
| --- | --- | --- |
| Base URL | `Ai:BaseUrl` | `AI_BASE_URL` |
| API key | `Ai:ApiKey` | `AI_API_KEY` |
| Model | `Ai:Model` | `AI_MODEL` |
| Key source | `Ai:CredentialSource` (`auto`/`windows`/`environment`) | `AI_CREDENTIAL_SOURCE` |
| Timeout | `Ai:TimeoutSeconds` | `AI_TIMEOUT_SECONDS` |

## Exchange — explicit authentication, no fallback

Exchange authentication is **explicit** and **single-mode**. There is no
Windows → username/password (or reverse) fallback anywhere.

```text
Windows
→ current Windows/process identity (UseDefaultCredentials)
→ no credential fallback
→ a rejected identity is a clear authentication error

UsernamePassword
→ supplied EXCHANGE_USERNAME / EXCHANGE_PASSWORD (WebCredentials,
  EXCHANGE_DOMAIN optional)
→ no Windows fallback (the Windows identity is never used)
→ invalid credentials are a clear authentication error
```

### Configuration validation

The mode lives in `Exchange:Authentication` (`EXCHANGE_AUTHENTICATION`) and is
validated **before any Exchange attempt**. A per-user configuration saved in Settings
(mode `UsernamePassword`) requires a username and a password; the deployment
configuration is validated at startup (`ValidateOnStart`):

* `Windows` — username/password are not required (and are never used).
* `UsernamePassword` — a username and a password are both required.
* anything else is rejected as a configuration error.

`ExchangeOptions.GetConfigurationError(options[, passwordProvided])` is the single
source of truth for that message (used by the options validation, the EWS service
factory, the connection tester and the Settings save path — the save path passes
`passwordProvided: false` because a blank password input means "keep the stored one",
and the caller has already verified that one exists).

The same mode is used for every mail operation, for `/health/exchange` and for
`POST /api/settings/exchange/test`. A
failed attempt (HTTP 401/403, auth failure) is classified
`ExchangeMailErrorKind.Authentication` and surfaces as a clear error; it is
never retried with the other mode's credentials. Connectivity, timeout,
mailbox and other non-authentication failures keep their own classification
(`EwsErrorClassifier`) and are likewise not used to switch credentials.

Passwords never appear in logs, exceptions, health responses, settings/status
API responses or diagnostics. `GET /api/settings/exchange` returns the effective
endpoint/mode, `userManaged`, the per-user account (only when the user manages
Exchange here) and credential-presence booleans (`usernameConfigured` /
`passwordConfigured` / `hasPassword`) — never a password and never a
deployment-managed account value.

## Per-user configuration (desktop)

Everything a user saves on the Settings page has ONE non-secret home and one
secret home per credential:

```text
%APPDATA%\EmailAI\settings.json      (non-secret, per Windows user)
  { "exchange": { "ewsUrl", "authentication", "username", "domain" },
    "ai":       { "baseUrl", "model" } }

Windows Credential Manager           (secrets, per Windows user)
  EmailAI/AI        - AI provider API key
  EmailAI/Exchange  - Exchange password (UsernamePassword mode only)
```

* **Precedence** (`ExchangeConfigurationProvider`): a complete per-user Exchange
  section (endpoint present) is authoritative **as a whole** — endpoint, mode,
  username, domain and the password from `EmailAI/Exchange`. Without it the
  deployment's environment/appsettings configuration is used unchanged, including
  `EXCHANGE_PASSWORD`. There is no per-field merge and no credential mixing.
  Deployment-owned knobs (mailbox, Exchange version, timeout) keep their values.
* **Writes** (`ExchangeSettingsService`): everything is validated through
  `GetConfigurationError` *before* anything is stored, so a rejected save has no side
  effects. The password is written to its own target first and the document second;
  switching to `Windows` mode deletes any stored password.
* **Robustness** (`FileUserSettingsStore`): a missing/empty file means "no settings",
  a corrupt file is quarantined once as `settings.json.corrupt` and reported as "no
  settings", writes are atomic (temp file + replace), and the earlier
  `ai-settings.json` is migrated once when `settings.json` does not exist yet.
* **API**: `GET/PUT/DELETE /api/settings/exchange` and
  `POST /api/settings/exchange/test` (draft probe, never persisted) mirror the AI
  surface. Secrets are accepted on PUT and never echoed back.

### Env/config surface

| Setting | Configuration | Environment |
| --- | --- | --- |
| EWS endpoint | `Exchange:EwsUrl` | `EXCHANGE_EWS_URL` |
| Mode | `Exchange:Authentication` (`Windows`/`UsernamePassword`) | `EXCHANGE_AUTHENTICATION` |
| Domain | `Exchange:Domain` | `EXCHANGE_DOMAIN` |
| Username | `Exchange:Username` | `EXCHANGE_USERNAME` |
| Password | `Exchange:Password` | `EXCHANGE_PASSWORD` |
| Impersonated mailbox | `Exchange:Mailbox` | `EXCHANGE_MAILBOX` |
| Version / timeout | `Exchange:ExchangeVersion` / `Exchange:TimeoutSeconds` | `EXCHANGE_VERSION` / `EXCHANGE_TIMEOUT_SECONDS` |

### Current-user identity (the mailbox owner)

`IMailboxIdentityProvider` (`EwsMailboxIdentityProvider`) answers "who is the current
user?" from the Exchange context the application already authenticates with:

1. the mailbox configured for impersonation (`Exchange:Mailbox`) - the mailbox whose mail
   is being read,
2. the account configured for `UsernamePassword`,
3. the Windows/process account (`Windows` mode),

then resolves that candidate through the Exchange **directory** (`ResolveNames`), which is
what makes the display name and SMTP address authoritative AD data. When Exchange cannot
answer (not configured, unreachable, rejected) the locally known non-secret values are
returned instead, flagged with their weaker `Source`. The call never throws and is cached
per configuration fingerprint (endpoint/mode/account/mailbox - never the password) for a
bounded time, so a settings change is picked up without a restart. The identity is shown in
Settings (`currentUser`); a value supplied by the deployment configuration is suppressed in
the API response exactly like the deployment account fields, while the AI always receives
the full identity server-side.

## Opt-in real integration tests

The normal unit suite never requires a live service. Two opt-in integration
test files run only when their environment is present; when absent they report
"not configured" and complete without calling any service:

* AI: `EMAILAI_AI_INTEGRATION_TEST=true` plus `EMAILAI_AI_BASE_URL`,
  `EMAILAI_AI_API_KEY`, `EMAILAI_AI_MODEL` — issues one real chat completion
  and asserts a valid non-empty response.
* Exchange: `EMAILAI_EXCHANGE_INTEGRATION_TEST=true` plus the `EXCHANGE_*`
  variables — runs a real EWS root-folder probe through the single configured
  mode (Windows or UsernamePassword).

Real credentials are never committed to source control and never printed.

## Key implementation files

| Concern | Files |
| --- | --- |
| AI options + validation | `src/EmailAI.Domain/AI/AiOptions.cs` |
| AI settings API/credentials | `src/EmailAI.Api/Endpoints/SettingsEndpoints.cs`, `src/EmailAI.Api/Web/AiSettingsContracts.cs`, `src/EmailAI.Api/Configuration/AiCredentialCoordinator.cs` |
| AI HTTP client | `src/EmailAI.Infrastructure/AI/OpenAiCompatClient.cs`, `AiEndpointBuilder.cs` |
| AI unit tests | `tests/EmailAI.Tests/AiEndpointBuilderTests.cs`, `OpenAiCompatClientTests.cs`, `AiClientOpenAiIndependenceTests.cs`, `AiIntegrationTests.cs`, `AiPromptsTests.cs`, `AiServiceTests.cs`, `AiReadinessServiceTests.cs`, `AiReadinessEndpointTests.cs`, `AiTextDirectionTests.cs` |
| Current user (identity) | `src/EmailAI.Domain/Exchange/MailboxIdentity.cs`, `src/EmailAI.Application/Exchange/IMailboxIdentityProvider.cs`, `src/EmailAI.Infrastructure/Exchange/EwsMailboxIdentityProvider.cs`, `tests/EmailAI.Tests/MailboxIdentityTests.cs` |
| Mail HTML + new-mail feed | `src/EmailAI.Application/Mail/MailHtmlSanitizer.cs`, `src/EmailAI.Application/Notifications/*`, `src/EmailAI.Api/Endpoints/NotificationEndpoints.cs`, `tests/EmailAI.Tests/MailHtmlSanitizerTests.cs`, `MailNotificationServiceTests.cs`, `NotificationEndpointsTests.cs` |
| Live status refresh (UI) | `src/EmailAI.Api/Web/AppStatusNotifier.cs`, `tests/EmailAI.Tests/AppStatusNotifierTests.cs` |
| Exchange options + validation | `src/EmailAI.Domain/Exchange/ExchangeOptions.cs`, `src/EmailAI.Api/Configuration/ExchangeConfiguration.cs` |
| Exchange auth runner/factory | `src/EmailAI.Infrastructure/Exchange/ExchangeAuthRunner.cs`, `ExchangeServiceFactory.cs` |
| Exchange service + classifier | `src/EmailAI.Infrastructure/Exchange/EwsExchangeMailService.cs`, `EwsErrorClassifier.cs`, `EwsExchangeConnectionTester.cs` |
| Per-user settings store + secrets | `src/EmailAI.Infrastructure/Settings/FileUserSettingsStore.cs`, `WindowsSecretStore.cs`, `UserSettingsAiStore.cs`, `src/EmailAI.Infrastructure/AI/WindowsCredentialStore.cs` |
| Effective config + settings writes | `src/EmailAI.Infrastructure/Settings/ExchangeConfigurationProvider.cs`, `ExchangeSettingsService.cs` |
| Exchange tests | `tests/EmailAI.Tests/ExchangeConfigurationTests.cs`, `ExchangeAuthModeTests.cs`, `ExchangeConfigurationProviderTests.cs`, `ExchangeSettingsServiceTests.cs`, `ExchangeSettingsEndpointsTests.cs`, `FileUserSettingsStoreTests.cs`, `ExchangeIntegrationTests.cs` |

