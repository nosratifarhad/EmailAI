# 02 - Exchange configuration

**Purpose.** Define which Exchange settings are in effect at any moment, so mail operations
never mix credentials from two sources.

**Scope.** Endpoint, authentication mode, account, mailbox, version and timeout; precedence
between deployment and per-user configuration. Password storage is specified in 13;
authentication semantics in 03.

**Inputs.** Per-user document `%APPDATA%\EmailAI\settings.json`, the per-user credential store,
process environment (`EXCHANGE_*`), `appsettings.json` placeholders.

**Outputs.** `ExchangeOptions` - the *effective* configuration handed to the EWS client for the
request in progress, plus a status object for the Settings API.

**Responsibilities.** `ExchangeConfigurationProvider` (effective read, per request),
`ExchangeSettingsService` (the only writer), `ExchangeConfiguration` (startup validation of the
environment half), `SettingsEndpoints` (HTTP surface).

**Invariants.**

1. A saved per-user Exchange configuration is authoritative **as a whole** (endpoint, mode,
   account, password); deployment values are never merged into it field by field.
2. Environment values always win over `appsettings.json`.
3. The documentation placeholder host (`mail.example.com` and friends) is never treated as a
   real endpoint: the app reports "not configured" instead of touching the network.
4. The password is only ever read when the effective mode is `UsernamePassword`.
5. Reading the effective configuration never throws for a credential-store problem on a path
   that is not used (for example a missing password in `Windows` mode).
6. Delete returns the account to the deployment configuration (document + secret removed).

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| No configuration at all | `/health/exchange` 503 `exchange_not_configured`; mail endpoints answer a typed "not configured" error; the UI offers Settings |
| Placeholder endpoint | Treated as not configured (warning logged at startup) |
| Saved configuration invalid (unsupported mode, missing account/password, non-absolute or placeholder URL) | `PUT`/`POST /test` answer 400 with a message and write nothing |
| Credential store failure | Typed configuration error surfaced through the middleware; no secret in the text |
| Configuration changed while running | The next operation reads the new effective configuration (per-request read); no restart required |

**Security constraints.** No secret may be returned by any GET/health response: the API reports
presence booleans and, for the current user, non-secret display data only (see 04, 13).
Deployment-managed account values are not echoed back.

**Configuration ownership.** Administrator/deployment: `EXCHANGE_EWS_URL`,
`EXCHANGE_AUTHENTICATION`, `EXCHANGE_DOMAIN/USERNAME/PASSWORD`, `EXCHANGE_MAILBOX`,
`EXCHANGE_VERSION`, `EXCHANGE_TIMEOUT_SECONDS` (or appsettings for a self-hosted instance).
Desktop user: the same fields through **Settings → Exchange connection** (their values are
per Windows account).

**Implementation.** `src/EmailAI.Domain/Exchange/ExchangeOptions.cs`,
`src/EmailAI.Api/Configuration/ExchangeConfiguration.cs`,
`src/EmailAI.Infrastructure/Settings/ExchangeConfigurationProvider.cs`,
`ExchangeSettingsService.cs`, `src/EmailAI.Api/Endpoints/SettingsEndpoints.cs`,
`src/EmailAI.Api/Web/AiSettingsContracts.cs` (shared settings contracts).

**Tests.** `ExchangeConfigurationTests`, `ExchangeConfigurationProviderTests`,
`ExchangeSettingsServiceTests`, `ExchangeSettingsEndpointsTests`, `FileUserSettingsStoreTests`.
