# EmailAI service context (authoritative behaviour)

This document is the authoritative summary of the two external service integrations
(AI provider and Exchange). Keep it in sync whenever the behaviour changes.

The per-area specifications - architecture, Exchange configuration/authentication, identity,
AI architecture/readiness/prompt trust boundaries, rendering security, notifications, settings
synchronization, initial Inbox loading, the desktop lifecycle, secret management, testing and
release - live in [`spec/README.md`](spec/README.md) (`.ai/spec/`). This file stays the quick
reference; the specification set is the detailed contract and the two must never contradict each
other.

## Contents

| Area | Section |
| --- | --- |
| Why the system exists | [System purpose](#system-purpose) |
| Processes, layers, traffic | [Architecture](#architecture) |
| Ownership | [Project boundaries](#project-boundaries) |
| Start-up and request path | [Runtime flow](#runtime-flow) · [Request flows](#request-flows) |
| AI provider | [AI - configurable OpenAI-compatible provider](#ai---configurable-openai-compatible-provider) |
| Exchange authentication | [Exchange - explicit authentication, no fallback](#exchange---explicit-authentication-no-fallback) |
| Configuration and secrets | [Per-user configuration](#per-user-configuration-desktop) · [Secret storage](#secret-storage) · [Configuration precedence](#configuration-precedence) |
| Identity and trust | [Current-user identity](#current-user-identity-the-mailbox-owner) · [Prompt trust boundaries](#prompt-trust-boundaries) |
| Rendering and notifications | [HTML sanitization](#html-sanitization) · [Notifications](#notifications) |
| UI behaviour | [UI behaviour](#ui-behaviour-settings-and-mail) · [App status refresh](#app-status-refresh) · [Initial inbox loading](#initial-inbox-loading) |
| Failures | [Error handling](#error-handling) |
| Verification and shipping | [Testing strategy](#testing-strategy) · [Release and package model](#release-and-package-model) · [Known limitations](#known-limitations) |

## System purpose

EmailAI is a **Windows desktop mail client for Microsoft Exchange** with a built-in AI assistant. It
exists to give a user three things a generic mail client does not:

1. **Their own mail, on their own machine, through their own Exchange account** - folders, messages,
   conversation threads and replies over EWS, with no third-party mail service in the middle.
2. **An AI assistant that knows who it is helping** - summarization, suggested replies and draft
   generation are given the authoritative mailbox identity next to the email content, and the
   assistant never guesses the user, never invents a signature and never sends anything by itself.
3. **Configuration that belongs to the user, not to the deployment** - any OpenAI-compatible provider
   and any EWS endpoint, configured per Windows account inside the application, with the secrets
   stored in the Windows Credential Manager.

It is deliberately **not**: a mail server, a gateway or proxy for AI traffic, a multi-tenant service,
a cloud product, or a place where mail is stored, indexed or analyzed in the background.

## Architecture

```text
Windows desktop
  Electron shell (desktop/main.js)                     one window, one app instance
    - takes a single-instance lock
    - picks a free 127.0.0.1 port
    - starts resources/server/EmailAI.Api.exe --urls http://127.0.0.1:<port>
    - waits for GET /health, then loads the UI in a sandboxed BrowserWindow
    - polls GET /api/notifications/mail every 30 s -> native Windows toasts
    - terminates the child on quit (no orphan process)
        | loopback HTTP only - never an inbound port
  EmailAI.Api (ASP.NET Core, .NET 10, self-contained win-x64)
    Components/      Blazor Web App UI (Interactive Server) - the only UI
    Endpoints/       REST: /api/folders, /api/messages, /api/ai, /api/settings,
                     /api/notifications, /health[/exchange|/ai]
    Web/             typed UI client, API contracts, status notifier
    Middleware/      typed JSON error envelope
  EmailAI.Application   AI prompts/services/limits, HTML sanitizer, notification detection,
                        Exchange and AI seams, settings/secret contracts
  EmailAI.Domain        DTOs and option types (mail, Exchange, AI, per-user settings, identity)
  EmailAI.Infrastructure EWS client, OpenAI-compatible client, per-user file store, secret store
        |
   Microsoft Exchange (EWS over HTTPS)        your OpenAI-compatible provider (HTTPS)
```

The UI is C#/Razor served by the same process as the API - one origin, no CORS, no second web server
and no front-end build step. EmailAI is a client of exactly two external services (Exchange and the
AI provider the user configured).

## Project boundaries

| Project | Owns | Must not |
| --- | --- | --- |
| `EmailAI.Domain` | Pure types: mail DTOs, `ExchangeOptions`/`AiOptions` (with validation), `MailboxIdentity`, the per-user settings records, notification records | Perform I/O or reference infrastructure/ASP.NET |
| `EmailAI.Application` | Use-case seams and logic: `IExchangeMailService`, `IAiService` + prompts + limits, `MailHtmlSanitizer`, notification detection, the `ISecretStore`/`IUserSettingsStore` contracts | Know about EWS, HTTP clients, files or the Credential Manager |
| `EmailAI.Infrastructure` | The only place that talks to the outside world: EWS (`EwsExchangeMailService`, identity provider, connection tester, error classifier), the AI HTTP client (`OpenAiCompatClient`), the per-user file store and the Windows secret store | Contain UI, routing or business prompts |
| `EmailAI.Api` | The host: endpoint mapping, DI wiring, the Blazor components (UI), JSON contracts, error middleware | Implement EWS/AI transport itself |
| `desktop/` | The Electron shell and the packaging pipeline (`main.js`, `scripts/release.js`, `scripts/verify-secrets.js`, `package.json`) | Hold application logic, secrets or configuration |
| `tests/EmailAI.Tests` | Every automated layer (see 14) | Touch the developer's real credentials, mailbox or settings files |

No layer hard-codes a vendor: no provider name, base URL or model identifier exists outside the
OpenAI-compatible contract, and no Exchange endpoint exists outside the sample configuration.

## Runtime flow

1. The user launches **EmailAI**. The shell takes a single-instance lock, so a second launch focuses
   the existing window instead of starting a second backend.
2. The shell picks a free loopback port and starts the bundled `EmailAI.Api.exe` with
   `--urls http://127.0.0.1:<port>` and `ASPNETCORE_ENVIRONMENT=Production`. If the backend does not
   become healthy it retries with a new port a bounded number of times, with its own log per attempt.
3. The backend binds **loopback only**, reads `appsettings.json` (documentation placeholders), then
   the environment (which always wins), composes its services, warns - without failing - when Exchange
   or AI is not configured, and starts serving.
4. The shell polls `GET /health` until it answers, shows the window and navigates to
   `http://127.0.0.1:<port>/`. The page is prerendered (the Inbox is already in the HTML) and then
   becomes an interactive Blazor Server circuit.
5. The shell starts polling `GET /api/notifications/mail?folder=inbox` every 30 s; the first poll is a
   silent baseline, so starting the app never notifies about existing mail.
6. On quit the shell stops polling and terminates the backend process - no orphaned
   `EmailAI.Api.exe` survives the window.

Per request, the backend stays local except for EWS SOAP calls to the configured Exchange endpoint
and - only for an AI action the user triggered - one HTTPS call to the configured AI provider.

## Request flows

### Exchange flow

```text
UI (Blazor) -> EmailApiClient -> GET /api/folders/{folder}/messages | /api/messages/{id} |
                                 /api/messages/{id}/thread | POST /api/messages/{id}/reply
  -> IExchangeMailService (EwsExchangeMailService)
       -> ExchangeConfigurationProvider : the effective endpoint/mode/account
                                         (per-user as a whole, else deployment configuration)
       -> ExchangeServiceFactory        : one ExchangeService per operation, in exactly the
                                         configured mode (Windows | UsernamePassword)
       -> EWS (FindItems / GetItem / SendAndSaveCopy / ResolveNames / ...)
       -> EwsErrorClassifier            : typed failure (authentication / connectivity / timeout /
                                         mailbox / not-configured / invalid configuration)
  -> JSON DTOs to the UI (never a credential, never a raw EWS fault)
```

### AI flow

```text
UI (Blazor AI panel)
  -> POST /api/ai/messages/{id}/(summarize|thread-summarize|suggest-reply|generate-reply)
     body: { "language": "Auto" | "English" | "Persian" }   (optional; names or numbers)
  -> AiEndpoints       : loads the message/thread through EWS (bounded), resolves the current user
  -> AiReadinessService: pre-flight check (local configuration + optional {BaseUrl}/models probe)
  -> AiService         : builds the prompt - trusted identity outside the fenced untrusted email text
  -> IAiCredentialProvider (AiCredentialCoordinator) : the runtime API key
  -> OpenAiCompatClient: POST {BaseUrl}/chat/completions, Authorization: Bearer <key>
  -> plain text back to the UI (the client decides RTL/LTR); nothing reaches the mailbox
```

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
* An AI operation's request body is `{"language":"Auto"|"English"|"Persian"}` - the
  documented **name**, case-insensitive; the numeric `AiLanguage` value (0/1/2) is still
  accepted for older clients. An omitted language means Auto, and an unsupported value answers
  **400 with an actionable message** instead of the framework's empty 400
  (`AiLanguageRequest`, pinned by `AiLanguageRequestTests` + `AiLanguageEndpointTests`). The
  response-language direction (RTL/LTR) is decided on the client from the selected language and
  the produced text (`AiTextDirectionResolver`).
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
  {
    "exchange": {
      "ewsUrl": "https://mail.contoso.com/EWS/Exchange.asmx",
      "authentication": "Windows",
      "username": "svc-mail",
      "domain": "CONTOSO"
    },
    "ai": {
      "baseUrl": "https://ai.your-company.example.com/v1",
      "model": "gpt-5"
    }
  }

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

Every candidate is tried in **one authenticated EWS session**, and a `DOMAIN\user` account is
also tried as the **bare account name**: a live Exchange directory answers for the bare
sAMAccountName and returns NO RESULTS for the qualified form, so sending only the qualified name
would silently degrade the identity to the local Windows account. The first candidate that
resolves wins (`ResolveCandidates`, pinned by `MailboxIdentityCandidatesTests`).

The winning candidate is then resolved through the Exchange **directory** (`ResolveNames`), which is
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

## Authentication model

EmailAI has exactly three credential-ish inputs, and they are never interchangeable:

| Input | What it is | Where it comes from | Where it is used |
| --- | --- | --- | --- |
| Windows identity | The account the EmailAI process runs as | The operating system, read-only | `Windows` mode (Exchange), and as the last identity candidate |
| Explicit account | A username (optional domain) and password | The Settings page (password in the Credential Manager) or `EXCHANGE_*` | `UsernamePassword` mode only |
| AI API key | The bearer token for the configured provider | Credential Manager target `EmailAI/AI`, or `AI_API_KEY` | The `Authorization` header of provider calls only |

The **mailbox identity** (`MailboxIdentity`) is *not* a credential: it is read-only display data
resolved through the Exchange directory (see below) and carries no password and no token.

### Exchange authentication modes

```text
Windows          -> current Windows/process identity (UseDefaultCredentials) ONLY
                    no username/password, no stored secret, no fallback
UsernamePassword -> the explicit account (WebCredentials, EXCHANGE_DOMAIN optional) ONLY
                    the Windows identity is never used, no fallback
```

The mode is explicit configuration (`Exchange:Authentication` / `EXCHANGE_AUTHENTICATION`), validated
**before** any Exchange attempt (`ExchangeOptions.GetConfigurationError`): `UsernamePassword` requires
a username and a password, `Windows` neither needs nor uses them, and anything else is a
configuration error. The same single mode is used by every mail operation, `/health/exchange` and
`POST /api/settings/exchange/test`.

### No-fallback policy

There is **no credential fallback anywhere**, in either direction:

* A rejected Windows identity is an authentication failure - EmailAI never retries with a stored
  username/password and never negotiates NTLM as a second attempt.
* Rejected explicit credentials are an authentication failure - EmailAI never falls back to the
  Windows identity.
* The AI client never retries with a different key, and never retries unauthenticated after a 401/403.
* Configuration errors, connectivity failures and timeouts are classified separately
  (`EwsErrorClassifier`, `AiException`) and are never a reason to switch credentials.

The only retry in the product is the *transport* retry of the initial Inbox load - one bounded retry
for a transient connection failure - and it never changes credentials, mode or endpoint.

## Secret storage

Secrets live **only** in the per-user Windows Credential Manager (`CredRead`/`CredWrite`/`CredDelete`),
one target per secret:

| Target | Secret | Writer | Reader |
| --- | --- | --- | --- |
| `EmailAI/AI` | AI provider API key | `PUT /api/settings/ai` (Settings page) | `AiCredentialCoordinator` before each provider call |
| `EmailAI/Exchange` | Exchange password (`UsernamePassword` mode) | `ExchangeSettingsService` (Settings page) | `ExchangeServiceFactory` when building the service |

Read/write/delete semantics (`ISecretStore`, `WindowsSecretStore`):

* `ExistsAsync(target)` / `ReadAsync(target)` report "no value" (false/null) for `ERROR_NOT_FOUND`
  (1168) and `ERROR_NO_SUCH_LOGON_SESSION` (1312); any other Win32 failure throws
  `SecretStoreException` carrying the error code and the target name - **never the value**.
* `WriteAsync(target, value)` rejects an empty/whitespace value, writes a generic credential with
  `Persist = CRED_PERSIST_LOCAL_MACHINE` and the current user name, and never logs the blob.
* `DeleteAsync(target)` is a no-op when nothing is stored; another failure throws the same typed error.
* Targets are independent by construction, so one secret can never be read, overwritten or deleted by
  accident while the other is being managed.
* On non-Windows hosts the store refuses to operate (use `AI_CREDENTIAL_SOURCE=environment` there).

The stored value is returned by exactly one member (`ReadAsync`) and reaches exactly one consumer (the
outbound request). It is never logged, never cached to disk, never echoed by an API/health response,
never part of an exception message and never displayed back in the UI.

## Configuration precedence

From highest to lowest priority, for both Exchange and AI:

1. **Per-user configuration** (Settings; `%APPDATA%\EmailAI\settings.json` + the Credential Manager).
   A complete per-user Exchange section (endpoint present) is authoritative **as a whole** - endpoint,
   mode, username, domain and the password from `EmailAI/Exchange`. There is no per-field merge and no
   credential mixing; deployment-owned knobs (impersonated mailbox, Exchange version, timeout) keep
   their deployment values. For AI, the per-user Base URL/model override the deployment values and the
   key follows the credential policy below. Removing the per-user settings returns the account to the
   deployment configuration.
2. **Process environment** (`EXCHANGE_*`, `AI_*`) - user/machine environment variables (the desktop
   shell forwards the environment it inherited) or the container/host environment.
3. **`appsettings.{Environment}.json`** - development logging only, and **not part of the released
   product**: a Release publish and the installer deliberately exclude `appsettings.Development.json`.
4. **`appsettings.json`** - the shipped sample: documentation placeholders only (`mail.example.com`,
   empty AI Base URL and model, and no credential property at all).
5. **Built-in code defaults** - never contain credentials.

Consequences: an environment variable always beats the sample appsettings values, and a placeholder
host is never treated as a real endpoint (it is reported as "not configured" and never contacted).

The AI key policy is selected by `Ai:CredentialSource` (`AI_CREDENTIAL_SOURCE`): `auto` (the per-user
store wins; the environment key is the fallback while the store is empty), `windows` (the store is the
only source) and `environment` (the environment key is the only source - the Settings page then
reports the key as server-managed and rejects save/remove). Values are never copied from one source
into another, and no secret is ever written to a non-secret file.

## Prompt trust boundaries

| Data | Trust | How it is delivered |
| --- | --- | --- |
| The current user (`MailboxIdentity`: display name, SMTP address, account) | **Trusted** | A dedicated system-prompt block, outside the untrusted fences, with aliases derived only from those authoritative values |
| The user's explicit choices (operation, response language) | **Trusted** | Request parameters turned into prompt instructions |
| Subject, sender, recipients, body text, thread history | **Untrusted data** | Wrapped in explicit data fences, prefixed by an instruction that this is data to analyse and never instructions to follow |
| Attachments | Not read | Only attachment *metadata* is listed; no attachment content ever reaches a prompt |
| Credentials (Exchange password, AI key) | Never present | No prompt, log, error or response contains them |

Every prompt (`AiPrompts`) states that email content is untrusted data, that instructions found inside
it must not be followed, that facts, names, signatures and actions must not be invented, and that when
the current user could not be resolved the answer must say so instead of guessing. Truncation
(`AiLimits`) bounds every input, so a hostile or enormous message cannot exhaust a request. The prompt
is the only place mail text leaves the machine, and only for an action the user triggered.

## HTML sanitization

Mail HTML is treated as **untrusted input** and neutralised in two independent layers:

1. **Sanitization** (`MailHtmlSanitizer`): scripts, nested frames/iframes, objects, forms, inline event
   handlers (`on*`), dangerous URL schemes (`javascript:`, `vbscript:`, `data:text/html`) and
   obfuscated spellings are removed, and the filter runs until the document is stable, so nesting
   tricks such as `<scr<script>ipt>` cannot re-form a tag.
2. **Isolation**: the sanitized body is placed in an `<iframe>` using `srcdoc`, the `sandbox` attribute
   **without** `allow-scripts`, and a restrictive `Content-Security-Policy`. Links open in a new window
   with `rel="noopener noreferrer"`.

Invariants: sanitization is never weakened for convenience; script execution is impossible in both
layers; the plain-text extraction is always available as a toggle; and a message with no body renders
an explicit "no body" state instead of a blank pane. Remote images follow normal browser behaviour and
are therefore subject to the network/proxy in use - documented, not silently rewritten.

## Notifications

```text
backend: IMailNotificationService (per-mailbox high-water mark, de-duplication by item id)
   -> GET /api/notifications/mail?folder=inbox        (sender + subject + receive time only)
        <- the Electron main process polls every 30 s
             -> first poll = silent baseline
             -> one native Windows toast per new item (at most 5 per poll, oldest first)
             -> click: focus the window and open /?item=<id>
```

* The **backend** decides what is new (ids already reported, high-water mark), so re-rendering,
  re-polling, a page reload or a folder switch can never produce a duplicate notification.
* The **payload carries headers only** - never a body, never a credential.
* The **shell renders** the toast and handles the click; a notification failure is logged once per
  distinct error and can never break the mail UI.
* Starting the application is **quiet**: the first poll only establishes the baseline.
* The feature is disabled by `EMAILAI_DISABLE_NOTIFICATIONS=1`, on non-Windows hosts, or when the OS
  does not support notifications.

## UI behaviour (Settings and Mail)

**Mail page (`/`)** - folders, the paged message list of the selected folder, and the open message with
its detail (From/To/Cc/Bcc, date, attachments), an HTML/plain-text toggle, the conversation view, the
reply composer and the AI panel. It accepts the deep link `/?item=<id>` used by notification clicks. A
message that cannot be loaded shows a typed error with a retry action instead of an empty pane, and an
empty folder shows an explicit empty state instead of a blank list.

**Settings page (`/settings`)** - two independent cards:

* **AI provider**: Base URL, model and a masked, write-only API key, with *Save AI settings*,
  *Remove saved key* and *Test connection* (a real request through the same client; nothing persisted).
* **Exchange connection**: EWS URL, the explicit mode, the account/domain/password fields (only in
  `UsernamePassword` mode), *Test connection* (probes the draft, saves nothing), *Save Exchange
  settings* and *Remove saved settings*. The card also shows the detected Windows account and the
  mailbox the assistant acts as, with its provenance - a deployment-managed account is reported as
  "value not shown" instead of being echoed.

Both cards read the same per-user document and each secret from its own target, and neither ever
displays a stored secret. A save is validated before it has any side effect, and every failure is
rendered as an actionable message - never a raw HTTP status or exception text.

## App status refresh

`AppStatusNotifier` is a singleton fan-out. `POST`/`DELETE /api/settings/ai|exchange` publish a change
for the affected area, and every live UI circuit subscribed to it re-reads the header pills
(`/health`, `/health/exchange`, `/health/ai`) and reloads whatever that change invalidated: an Exchange
change re-lists the folder instead of leaving stale mail on screen, an AI change re-evaluates
readiness. The header additionally polls every 20 s as a safety net, so a change that happened outside
the app still converges. A subscriber that throws can never break the writer or the other subscribers.

## Initial inbox loading

The first folder load runs in the component initialization pipeline, so it happens **during
prerendering** and the result is carried into the interactive circuit through `PersistentComponentState`:
exactly one Exchange query, and a first paint that already contains the Inbox instead of an empty
shell. A **transient connection failure** on that very first EWS call - which a cold server can produce
- is retried exactly **once** (no delay, no credential change, no configuration change); a second
failure is surfaced to the user unchanged, with a retry action.

## Error handling

Every failure is typed before it is rendered:

| Layer | Type | Result |
| --- | --- | --- |
| Exchange transport/authentication/mailbox | `ExchangeMailException` + `ExchangeMailErrorKind` (classifier) | An actionable message and the right status (503 for connectivity/timeout/mailbox; authentication reported as such) - never a raw EWS fault and never a credential |
| AI | `AiException` + `AiErrorKind` (not configured, invalid configuration, authentication, rate limit, timeout, unavailable, invalid response) | An actionable message through `AiUserMessages`; `/health/ai` stays HTTP 200 with a sanitized state |
| Settings validation | `ExchangeOptions.GetConfigurationError` / AI option validation | 400 with a specific message and **no side effect** (nothing is stored) |
| Unexpected request failure | `ExceptionHandlingMiddleware` | A structured JSON envelope for API paths, the error page for HTML routes; no stack trace and no secret |
| Notification failures | Logged once per distinct error | The mail UI is unaffected |

`/health` always describes the application process: an unreachable Exchange server or AI provider never
turns the application itself unhealthy, so the shell's startup probe is never defeated by a
misconfigured integration.

## Testing strategy

| Layer | Verifies | Command |
| --- | --- | --- |
| Unit | Options/validation, endpoint building, prompts and injection wording, truncation, sanitization, direction resolution, identity/aliases, notification detection, credential policy, status fan-out | `dotnet test tests/EmailAI.Tests -c Release` (offline) |
| Host | The real ASP.NET Core host with in-memory stores: JSON contracts, status codes, no-secret guarantees, prerendered HTML including the retry behaviour | the same command |
| Packaging contract | The release-defining files: artifact name, NSIS shape, sample configuration, no development configuration, no private endpoint, the CI pipeline, the documentation, and the scan gate itself (executed against planted defects) | the same command (`ReleasePackagingTests`) |
| Opt-in real | One real chat completion and one real EWS probe, only when the environment is present | `EMAILAI_AI_INTEGRATION_TEST=true` / `EMAILAI_EXCHANGE_INTEGRATION_TEST=true` plus the `EMAILAI_*`/`EXCHANGE_*` variables |
| Shell | The Electron script is syntactically valid, and the real lifecycle starts, becomes healthy, loads the UI and stops without an orphan | `node --check desktop/main.js`; run with `EMAILAI_SMOKE_QUIT_MS` |
| Release | The installer exists, contains the fresh backend, carries no secret, no development configuration and no non-placeholder endpoint, and its SHA-256 is written | `cd desktop; npm run release` |

The default suite is fully offline and deterministic: no live provider, no mailbox, no network and no
real Credential Manager (in-memory doubles instead), and it never reads or writes the developer's own
`%APPDATA%\EmailAI\settings.json`. The opt-in suites skip cleanly (reporting "not configured") when
their environment is absent, and never use fake credentials. Spec 14 is the detailed contract.

## Release and package model

```text
source tree
  -> dotnet test (Release)                       every automated layer, offline
  -> dotnet build EmailAI.slnx -c Release        must be 0 warnings / 0 errors
  -> dotnet publish src/EmailAI.Api -c Release -r win-x64 --self-contained true
       -> desktop/aspnet-publish/                backend + the sample appsettings.json (Production),
                                                 deliberately without appsettings.Development.json
  -> electron-builder --win nsis                 -> desktop/dist/EmailAI-Setup-<version>.exe
                                                 (Electron shell in app.asar + resources/server/)
  -> node scripts/verify-secrets.js publish + packaged server
       -> fails on a secret value, a development-only appsettings file or a non-placeholder endpoint
  -> verify the installer and the staged backend, then write <installer>.sha256
```

`desktop/package.json` is the single source of truth: `version` names the installer
(`build.nsis.artifactName` = `EmailAI-Setup-${version}.exe`, x64 only, per-user NSIS) and
`desktop/scripts/release.js` reads both values instead of duplicating them. `desktop/dist/` and
`desktop/aspnet-publish/` are build output and are git-ignored - binaries are release assets, never
committed. `.github/workflows/release.yml` runs the same pipeline for a `v*.*.*` tag (plus a
source-tree scan) and attaches the installer and its checksum to the GitHub Release.

## Known limitations

* **Windows only** - the desktop shell, native notifications and the Credential Manager are
  Windows-specific; there is no macOS/Linux desktop build (the backend host itself is portable).
* **x64 only** - no x86 or ARM64 installer.
* **Read + reply slice** - no read/unread marking, no move, no delete, no compose-new, and no
  attachment download (attachment metadata is listed only).
* **Not code-signed** - Windows SmartScreen may warn on first launch. The pipeline can consume a
  certificate through `CSC_LINK`/`CSC_KEY_PASSWORD` without changing shape.
* **Notifications are polled, not pushed** - every 30 s, because Exchange offers this application no
  push channel, so a message can be announced up to ~30 s late.
* **Email content leaves the machine through the configured provider** - pointing the Base URL at a
  provider you do not trust is a real privacy risk; it is documented rather than prevented.
* **Bounded inputs** - very long threads are truncated to a representative slice per message, so a
  conversation summary is representative rather than exhaustive.
* **Draft quality** is entirely the configured model's.
* **One mailbox per Windows user** - a second mailbox means a second Windows account.
* **Installer publishing is a manual step in this working copy** - there is no Git remote here, so the
  release-asset upload is documented rather than automated (see 15).

## Key implementation files

| Concern | Files |
| --- | --- |
| AI options + validation | `src/EmailAI.Domain/AI/AiOptions.cs` |
| AI settings API/credentials | `src/EmailAI.Api/Endpoints/SettingsEndpoints.cs`, `src/EmailAI.Api/Web/AiSettingsContracts.cs`, `src/EmailAI.Api/Configuration/AiCredentialCoordinator.cs` |
| AI HTTP client | `src/EmailAI.Infrastructure/AI/OpenAiCompatClient.cs`, `AiEndpointBuilder.cs` |
| AI unit tests | `tests/EmailAI.Tests/AiEndpointBuilderTests.cs`, `OpenAiCompatClientTests.cs`, `AiClientOpenAiIndependenceTests.cs`, `AiIntegrationTests.cs`, `AiPromptsTests.cs`, `AiServiceTests.cs`, `AiReadinessServiceTests.cs`, `AiReadinessEndpointTests.cs`, `AiTextDirectionTests.cs` |
| AI operation language contract | `src/EmailAI.Api/Web/AiContracts.cs`, `src/EmailAI.Api/Web/AiLanguageRequest.cs`, `src/EmailAI.Api/Endpoints/AiEndpoints.cs`, `tests/EmailAI.Tests/AiLanguageRequestTests.cs`, `AiLanguageEndpointTests.cs` |
| Current user (identity) | `src/EmailAI.Domain/Exchange/MailboxIdentity.cs`, `src/EmailAI.Application/Exchange/IMailboxIdentityProvider.cs`, `src/EmailAI.Infrastructure/Exchange/EwsMailboxIdentityProvider.cs` (`ResolveCandidates`), `tests/EmailAI.Tests/MailboxIdentityTests.cs`, `MailboxIdentityCandidatesTests.cs` |
| Mail HTML + new-mail feed | `src/EmailAI.Application/Mail/MailHtmlSanitizer.cs`, `src/EmailAI.Application/Notifications/*`, `src/EmailAI.Api/Endpoints/NotificationEndpoints.cs`, `tests/EmailAI.Tests/MailHtmlSanitizerTests.cs`, `MailNotificationServiceTests.cs`, `NotificationEndpointsTests.cs` |
| Live status refresh (UI) | `src/EmailAI.Api/Web/AppStatusNotifier.cs`, `tests/EmailAI.Tests/AppStatusNotifierTests.cs` |
| Initial Inbox load + list resilience (UI) | `src/EmailAI.Api/Components/Pages/MailClient.razor` (initialization-pipeline load, `PersistentComponentState`, one bounded retry for a transient connection failure), `tests/EmailAI.Tests/InitialInboxLoadTests.cs` |
| Desktop shell + release packaging | `desktop/main.js`, `desktop/scripts/release.js`, `desktop/scripts/verify-secrets.js`, `desktop/package.json`, `.github/workflows/release.yml` |
| Specifications and release docs | `.ai/spec/README.md` + `01`-`15`, `.ai/service-context.md` (this file), `README.md`, `RELEASE-NOTES.md` |
| Exchange options + validation | `src/EmailAI.Domain/Exchange/ExchangeOptions.cs`, `src/EmailAI.Api/Configuration/ExchangeConfiguration.cs` |
| Exchange auth runner/factory | `src/EmailAI.Infrastructure/Exchange/ExchangeAuthRunner.cs`, `ExchangeServiceFactory.cs` |
| Exchange service + classifier | `src/EmailAI.Infrastructure/Exchange/EwsExchangeMailService.cs`, `EwsErrorClassifier.cs`, `EwsExchangeConnectionTester.cs` |
| Per-user settings store + secrets | `src/EmailAI.Infrastructure/Settings/FileUserSettingsStore.cs`, `WindowsSecretStore.cs`, `UserSettingsAiStore.cs`, `src/EmailAI.Infrastructure/AI/WindowsCredentialStore.cs` |
| Effective config + settings writes | `src/EmailAI.Infrastructure/Settings/ExchangeConfigurationProvider.cs`, `ExchangeSettingsService.cs` |
| Exchange tests | `tests/EmailAI.Tests/ExchangeConfigurationTests.cs`, `ExchangeAuthModeTests.cs`, `ExchangeConfigurationProviderTests.cs`, `ExchangeSettingsServiceTests.cs`, `ExchangeSettingsEndpointsTests.cs`, `FileUserSettingsStoreTests.cs`, `ExchangeIntegrationTests.cs` |

