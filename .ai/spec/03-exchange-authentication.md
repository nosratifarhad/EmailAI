# 03 - Exchange authentication

**Purpose.** Make Exchange authentication **explicit and single-mode**: every operation uses
exactly one configured credential mechanism, with no implicit fallback.

**Scope.** The two supported modes, when each is valid, and what happens on a rejected
attempt. Not in scope: which account is the mailbox owner (04).

**Inputs.** `ExchangeOptions.Authentication`, `Username`, `Domain`, `Password`, the current
Windows process identity.

**Outputs.** An authenticated EWS `ExchangeService` per operation, plus a classified result
(`healthy`, `authentication_failed`, `connectivity`, ...).

**Responsibilities.** `ExchangeOptions` (mode constants, validity helpers, configuration
error), `ExchangeServiceFactory` (mode → credentials), `ExchangeAuthRunner` (one attempt per
operation), `EwsExchangeMailService` / `EwsExchangeConnectionTester` (logging + classification).

**Invariants.**

1. `Windows` → `UseDefaultCredentials = true`; username/password are neither required nor used.
2. `UsernamePassword` → `WebCredentials` built from domain/username/password; both account and
   password are required and validated at startup *before* any Exchange attempt.
3. **No fallback of any kind**: a rejected attempt is never retried with another credential
   mechanism (no NTLM, no basic, no Windows identity in `UsernamePassword` mode).
4. Exactly one authentication attempt per operation; the mode used is logged (never a secret).
5. Unsupported mode strings are rejected (`ArgumentException`, HTTP 400) instead of being
   guessed; retired values (`NTLM`, `BASIC`) are invalid by design.
6. The authentication mode is part of the identity cache fingerprint, so changing it is noticed
   immediately.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Server rejects the identity (401/403) | Classified `Authentication` → HTTP 502 `exchange_authentication_failed`, message asks to check the configured credentials; **no retry with another mechanism** |
| Endpoint unreachable | Classified `Connectivity` → 503 `exchange_connection_failed`; the UI may retry once (see 11) |
| Server slow | Classified `Timeout` → 504 `exchange_timeout` |
| Mode not configured / invalid | 400 with a message on save/test; at startup the environment half is validated and logs a warning |
| TLS trust failure | Surfaced through the same classifier (no silent bypass, no certificate override) |

**Security constraints.** The password travels from the Settings page to the server once and is
stored in the Windows Credential Manager (13). It never appears in logs, health text, API
responses or exception messages; the probe tools print the account name only.

**Configuration ownership.** Administrator: `EXCHANGE_AUTHENTICATION` + credentials (or a
deployment `Windows` mode that uses the service account). Desktop user: chosen per user in
Settings; the mode is one of `Windows` or `Username + Password`.

**Implementation.** `src/EmailAI.Domain/Exchange/ExchangeOptions.cs`,
`src/EmailAI.Infrastructure/Exchange/ExchangeServiceFactory.cs`, `ExchangeAuthRunner.cs`,
`EwsExchangeMailService.cs`, `EwsExchangeConnectionTester.cs`, `EwsErrorClassifier.cs`,
`tools/EwsAuthProbe/Program.cs` (live diagnostic probe for both modes).

**Tests.** `ExchangeAuthModeTests`, `ExchangeConfigurationTests` (unsupported/retired modes),
`ExchangeSettingsServiceTests`, `ExchangeSettingsEndpointsTests`, `ExchangeIntegrationTests`
(opt-in, real EWS through the single configured mode).
