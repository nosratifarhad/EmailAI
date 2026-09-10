# EmailAI

ASP.NET Core (.NET 10) mail client for Exchange with **server-side AI** (any
OpenAI-compatible provider), delivered as **one application**: an ASP.NET Core backend
(EWS + AI) **plus** a Blazor Web App UI served from the same process.

```
Browser
   |
   v
http://localhost:8080   (ASP.NET Core - EmailAI.Api)
   |-- Blazor Web App UI (Interactive Server)
   |-- /api/folders/{folder}/messages          GET    message list (paged)
   |-- /api/messages/{itemId}                  GET    full message
   |-- /api/messages/{itemId}/thread           GET    conversation
   |-- /api/messages/{itemId}/reply            POST   send reply (EWS) - the ONLY sender
   |-- /api/ai/readiness                       GET    can the assistant be used right now?
   |-- /api/ai/messages/{itemId}/...           POST   AI operations (server-side)
   |-- /api/notifications/mail                 GET    new-mail feed for the desktop shell
   |-- /health | /health/exchange | /health/ai GET    status probes
   |-- Exchange via EWS (SOAP)
   `-- OpenAI-compatible AI provider (user-configurable) - called only by the server,
       never by the browser
```

## Solution layout

| Project | Responsibility |
| --- | --- |
| `src/EmailAI.Api` | Host: REST endpoints, **Blazor Web App UI**, typed UI API client, AI options binding |
| `src/EmailAI.Application` | `IExchangeMailService`, **`IAiService` / `AiService` / `AiPrompts`** (business logic + prompts) |
| `src/EmailAI.Domain` | DTOs and option types shared by every layer (`Mail`, `Exchange`, **`AI`**) |
| `src/EmailAI.Infrastructure` | **EWS implementation** and **`OpenAiCompatClient`** (OpenAI-compatible HTTP) |
| `tests/EmailAI.Tests` | xUnit tests for the AI layer, the mail/identity/notification surface and the host endpoints (fake HTTP handlers and in-memory Exchange - no live provider or mailbox) |
| `tools/EwsApiProbe` | Reflection probe used during EWS development |
| `tools/EwsAuthProbe` | Reproducible EWS authentication probes (explicit Windows / UsernamePassword modes) |

## How to run

```bash
dotnet build EmailAI.slnx          # must finish with 0 warnings / 0 errors
dotnet test tests/EmailAI.Tests    # AI unit + host tests (no live provider needed)
# Real integration tests are opt-in and run only when their environment is present (see "Tests").
#   EMAILAI_AI_INTEGRATION_TEST=true EMAILAI_AI_BASE_URL=https://.../v1 \
#     EMAILAI_AI_API_KEY=... EMAILAI_AI_MODEL=... dotnet test tests/EmailAI.Tests
dotnet run --project src/EmailAI.Api
```

- **Browser URL:** http://localhost:8080 (the `http` launch profile binds 8080).
- **API URL:** same origin - `http://localhost:8080/api/...` and `http://localhost:8080/health`.
- The launch profile sets `ASPNETCORE_ENVIRONMENT=Development`.

## Configuration

Exchange and AI are configured at runtime through environment variables (see
`.env.example`); the appsettings.json values that ship with the repository are
**documentation placeholders only**. Nothing is hard-coded and **no credential is
ever exposed to the browser**. The app starts fine without either - it reports the
missing state instead.

| Variable | Purpose |
| --- | --- |
| `EXCHANGE_EWS_URL` | EWS endpoint, e.g. `https://mail.example.com/EWS/Exchange.asmx` |
| `EXCHANGE_AUTHENTICATION` | explicit mode: `Windows` (current Windows identity only, no fallback) or `UsernamePassword` (explicit account only, no Windows fallback) |
| `EXCHANGE_DOMAIN` / `EXCHANGE_USERNAME` / `EXCHANGE_PASSWORD` | explicit credentials - REQUIRED in `UsernamePassword` mode, never used in `Windows` mode |
| `EXCHANGE_MAILBOX` | optional impersonated mailbox (empty = the account's own mailbox) |
| `EXCHANGE_VERSION`, `EXCHANGE_TIMEOUT_SECONDS` | optional tweaks |
| `AI_BASE_URL` | Base URL of your OpenAI-compatible provider (its API root), e.g. `https://api.openai.com/v1` |
| `AI_API_KEY` | bearer token for the provider (empty when the provider needs none) |
| `AI_CREDENTIAL_SOURCE` | `auto` (default), `windows` or `environment` - where the AI key comes from (see "Secret handling" below) |
| `AI_MODEL` | model identifier to request, exactly as the provider expects it, e.g. `gpt-5` |

### Configuration precedence

From highest to lowest priority:

1. **Per-user desktop configuration** (the **Settings** page, route `/settings`) - the
   non-secret values (Exchange endpoint/mode/account, AI Base URL/Model) are stored in
   one per-user document, `%APPDATA%\EmailAI\settings.json`, and the secrets (Exchange
   password, AI API key) in the Windows Credential Manager, scoped to the Windows
   account. A saved Exchange configuration is authoritative **as a whole** - endpoint,
   mode, account and password - and the deployment configuration below is never merged
   into it, so credentials from the two sources cannot be mixed. **Remove saved
   settings** deletes both halves and returns to the deployment configuration.
2. **Process environment** - `EXCHANGE_*` / `AI_*` (the desktop shell forwards the
   environment of the process that launched it, so on Windows set them as user or
   machine environment variables, or launch EmailAI from a console where they are
   set).
3. **appsettings.{Environment}.json** - only `appsettings.Development.json` exists
   in this repository (logging only).
4. **appsettings.json** - public sample placeholders. `Exchange:EwsUrl` points at
   the reserved `mail.example.com` host; when `EXCHANGE_EWS_URL` is not set and no
   user has configured Exchange in Settings, the server refuses to treat that host as
   a real endpoint and reports Exchange as "not configured" instead of failing on the
   network later.
5. **Built-in code defaults** - never contain credentials.

A runtime environment variable therefore **always overrides** the generic
appsettings values; the placeholders can never override real runtime configuration.


### Exchange authentication semantics (`EXCHANGE_AUTHENTICATION`)

Exchange authentication is **explicit and single-mode** - exactly one configured
mechanism and never an automatic fallback to another credential type:

```text
Windows          -> current Windows/process identity only
                    (no username/password, no NTLM fallback)

UsernamePassword -> EXCHANGE_USERNAME / EXCHANGE_PASSWORD only
                    (no Windows identity, no Windows fallback)
```

* `Windows` - every mail operation and `/health/exchange` authenticates with the
  current Windows/process identity (`UseDefaultCredentials`). Username and password
  are neither required nor used. If the server rejects the identity (HTTP 401 /
  authentication failure) the operation fails with a clear authentication error -
  nothing is silently retried with a username/password or NTLM mechanism.
* `UsernamePassword` - every operation authenticates with the explicit account from
  `EXCHANGE_DOMAIN` / `EXCHANGE_USERNAME` / `EXCHANGE_PASSWORD`. Both
  `EXCHANGE_USERNAME` and `EXCHANGE_PASSWORD` are required; the configuration is
  validated at startup, before any Exchange attempt. The Windows identity is never
  used and is never tried as a fallback.

A failed authentication attempt is never silently retried with the other mode.
`GET /api/settings/exchange` reports where the effective configuration comes from
(`userManaged`), the effective endpoint and mode, the detected Windows account
(`Windows` mode) and which credential fields are configured - booleans only, never
credentials. On the desktop the user can change all of it on the **Settings** page
(see "Secret handling"), which stores a complete per-user configuration; the
deployment variables below remain the fallback for accounts that have not.

### Mailbox semantics (`EXCHANGE_MAILBOX`)

`EXCHANGE_MAILBOX` is optional EWS **impersonation**: when empty the service opens
the authenticated account's own mailbox (the normal case). When set to an SMTP
address, every `ExchangeService` is created with
`ImpersonatedUserId = SmtpAddress(EXCHANGE_MAILBOX)`, letting an authorised service
account open that mailbox instead. It does not affect the EWS URL, credentials or
any other setting.

### AI configuration (OpenAI-compatible provider)

EmailAI requires no separate gateway software. It talks **directly** to the
OpenAI-compatible Chat Completions API of whatever provider you configure:

1. Open **Settings** (gear button in the header, route `/settings`).
2. Enter the **Base URL** of an OpenAI-compatible API, e.g. `https://api.openai.com/v1`
   or `https://your-company-ai.example.com/v1` (a trailing slash is fine).
3. Enter your **API key** (on Windows desktop it is stored in the Windows Credential
   Manager - never shown back, never sent to the browser).
4. Enter the **Model** identifier, e.g. `gpt-5`.
5. Click **Save AI settings**, then **Test connection**.

On servers/containers the same three values come from the environment instead:
`AI_BASE_URL`, `AI_MODEL` and `AI_API_KEY` (see `.env.example`). Desktop users can
override the server defaults from Settings without touching the environment.

EmailAI calls exactly:

```text
POST {AI_BASE_URL}/chat/completions      Authorization: Bearer {AI_API_KEY}
```

and sends the configured model plus the conversation messages.

## Secret handling

Credentials never travel with the shipped package. The desktop installer, the
bundled appsettings.json and every published file carry **no Exchange password and
no AI API key**; the release pipeline secret-scans them and refuses to build an
installer when any is found (see `desktop/scripts/verify-secrets.js`).

| Secret | Where it lives |
| --- | --- |
| Exchange password | On the Windows desktop: the **Windows Credential Manager** (target `EmailAI/Exchange`, scoped to the current Windows user), written only from the Settings page and never kept in JSON, logs, API responses or the installer. On servers/containers that have no per-user configuration: `EXCHANGE_PASSWORD` in the process/server environment. `EXCHANGE_AUTHENTICATION=Windows` uses only the current Windows identity and never keeps a password; `UsernamePassword` uses only the configured account's password. There is no automatic fallback between the two modes - a rejected attempt is a clear authentication error. |
| AI API key | Windows Credential Manager on desktop (target `EmailAI/AI`, scoped to the current Windows user) - written from the **Settings** page and read at runtime by the backend. `AI_API_KEY` is the server/CI fallback only. |

`AI_CREDENTIAL_SOURCE` decides the runtime policy:

- `auto` (default) - a key saved in Settings (Windows Credential Manager) is
  authoritative; `AI_API_KEY` is used only as a fallback while the store is empty.
- `windows` - the per-user secure store is the only source.
- `environment` - `AI_API_KEY` is the only source (CI, containers, servers); the
  Settings page reports the key as server-managed and rejects save/remove.

The **Settings** page (gear button in the header, route `/settings`) lets a desktop
user configure both integrations for their own Windows account:

* **AI provider** - Base URL and Model plus a masked API key field; the key can be
  saved, replaced or removed and is never displayed or returned by any endpoint. The
  page also tests the connection against the configured provider.
* **Exchange connection** - the EWS endpoint, the explicit authentication mode
  (`Windows` or `Username + Password`) and, in the second mode, the account and its
  password (a masked field, write-only). **Test connection** probes the draft through
  exactly the selected mode without saving anything; **Save Exchange settings** stores
  the non-secret values per user and the password in the Windows Credential Manager
  (target `EmailAI/Exchange`); **Remove saved settings** returns the account to the
  deployment configuration. The card also shows the automatically detected Windows
  account (domain/user/full name and how it was detected) that `Windows` mode would
  use, plus the **mailbox the assistant acts as** - the current user handed to the AI,
  with its provenance (a deployment-managed account shows as "value not shown").

Both cards read/write the same per-user document, while every secret stays in the
Credential Manager. No API or health endpoint ever returns a secret: `/api/settings/exchange`
reports the effective endpoint/mode plus `usernameConfigured` / `passwordConfigured` /
`hasPassword` booleans (a deployment-managed account is never echoed back), and
`/api/settings/ai` reports the effective Base URL/Model plus key presence only.
`/health/ai` distinguishes `connected` from `not_configured` (no provider configured,
or no per-user key yet), `authentication_failed` and `unavailable` with sanitized,
key-free messages.

The one identity value that is returned - `currentUser` - is display data only (a display
name, an SMTP address and/or an account name). A value that came from the **deployment**
configuration is suppressed the same way the account fields are; values resolved through
the Exchange directory are shown, because the mail list the user is already reading makes
that mailbox visible anyway. No password, token or key is ever part of it.


## How the Blazor UI talks to the backend

- The UI is hosted by `EmailAI.Api` itself. Pages/components live under
  `src/EmailAI.Api/Components`, and the typed client under `src/EmailAI.Api/Web`.
- Components use **`EmailApiClient`** (registered via DI, one per circuit) which calls
  the REST API with **same-origin** relative URLs - no CORS, no second server.
- Mail responses deserialize into the existing `EmailAI.Domain.Mail` DTOs; AI responses
  are plain text (`AiContentResult.Content`).

UI feature -> API used:

| UI feature | API |
| --- | --- |
| Header status pills (App / Exchange / AI) | `GET /health`, `/health/exchange`, `/health/ai` (polled every 20 s, re-checked immediately after a Settings save) |
| Folder navigation & message list | `GET /api/folders/{folder}/messages?offset&pageSize` |
| Message detail (From/To/Cc/Bcc, date, attachments, body) | `GET /api/messages/{itemId}` |
| Conversation view | `GET /api/messages/{itemId}/thread` |
| Reply / Reply-all (plain text sent as safe HTML) | `POST /api/messages/{itemId}/reply` |
| AI readiness hint + disabled AI buttons | `GET /api/ai/readiness` |
| **Summarize message** | `POST /api/ai/messages/{itemId}/summarize` |
| **Summarize thread** | `POST /api/ai/messages/{itemId}/thread-summarize` |
| **Suggest reply** | `POST /api/ai/messages/{itemId}/suggest-reply` |
| **Generate reply (editable draft)** | `POST /api/ai/messages/{itemId}/generate-reply` |
| Settings - AI provider card | `GET`/`PUT`/`DELETE /api/settings/ai`, `POST /api/settings/ai/test` |
| Settings - Exchange card | `GET`/`PUT`/`DELETE /api/settings/exchange`, `POST /api/settings/exchange/test` |
| Desktop new-mail notifications | `GET /api/notifications/mail?folder=inbox` (polled by the Electron main process) |

Every AI button takes an optional JSON body `{ "language": "Auto" | "English" |
"Persian" }` (default Auto = follow the email language).

Every AI operation also carries the **current user** - the mailbox identity resolved from
the Exchange account, never inferred from the email text (see "Current-user identity").

## Mail rendering, writing direction and readiness

- **HTML bodies are preferred over the plain text**: an opened message with
  `bodyHtml` renders that HTML, and a toggle switches back to the extracted plain text.
  The HTML is sanitised and shown inside a sandboxed, script-less `<iframe>` with a
  restrictive CSP (`MailHtmlSanitizer`), so mail can never script the app or the session
  (`srcdoc`, no `allow-scripts`). A message with no HTML falls back to the plain text,
  and a message with no body at all renders an explicit "no body" state - never a blank
  pane.
- **Writing direction follows the content**: AI output and the reply composer carry
  `dir="rtl"`/`dir="ltr"` (and matching alignment) decided by `AiTextDirectionResolver` -
  an explicit Persian/English choice wins, `Auto` follows the actual text
  (Persian/Arabic/Hebrew are right-to-left). No component hardcodes "Persian means right".
- **AI readiness is stated before it is attempted**: the mail page and the AI panel show
  a non-blocking banner from `GET /api/ai/readiness` ("not configured", "no API key
  saved yet", "endpoint unreachable", "key rejected", "rate limited", "timeout", ...)
  with an **Open Settings** action, and the AI buttons are disabled while it is not
  ready. A failed operation is mapped through the very same user-message table, so the
  user never sees a raw HTTP status or an exception message.
- **The header status updates immediately**: saving AI or Exchange settings in the
  Settings page publishes an `AppStatusNotifier` change, so the header pills re-check
  right away (they still poll every 20 s as a safety net), and an Exchange change
  re-lists the folder instead of leaving stale mail on screen.
- **The first paint contains the Inbox**: the initial folder load runs in the component
  initialization pipeline, so it happens during prerendering; the page is carried into
  the interactive circuit through `PersistentComponentState`, which means exactly one
  Exchange query and no empty first render.

## Current-user identity (who the AI is told it is helping)

The AI must never guess who the user is from the email content, so every AI operation is
given the **authoritative mailbox identity** (`MailboxIdentity`) resolved by
`IMailboxIdentityProvider` from the Exchange context EmailAI already authenticates with:

```text
1. the mailbox configured for EWS impersonation (EXCHANGE_MAILBOX)   <- most accurate
2. the account configured for UsernamePassword authentication
3. the Windows/process account
      -> resolved through the Exchange DIRECTORY (ResolveNames)
      -> display name + SMTP address are authoritative AD data
      -> if Exchange cannot answer, the locally known values are used instead
         (still non-secret; the identity is simply flagged as less authoritative)
```

* The identity is delivered to the model as **trusted context outside the untrusted-data
  fences**, with aliases derived only from the authoritative values, so "can you ask
  Farhad to approve this?" is understood as "the current user". The model is also told to
  follow that identity when the email text claims otherwise.
* Resolution never throws: an unreachable/not-configured Exchange degrades to the local
  account (`WindowsIdentity`) or `Unknown`, and mail/AI keep working. The result is cached
  per configuration fingerprint (endpoint/mode/account/mailbox - **never** the password)
  for a bounded time, so a settings change is picked up without a restart.
* The identity carries **no secret** (no password, no token). Settings shows it in the
  Exchange card ("Mailbox used by the assistant"), and `GET /api/settings/exchange`
  returns it as `currentUser` - except that a value supplied by the **deployment**
  configuration (a service account the user never typed) is never echoed back, exactly
  like the account fields. The AI still receives the full identity server-side.

## AI architecture

```
Blazor AI panel  ->  POST /api/ai/...  ->  AiEndpoints (loads message/thread via EWS)
                                          ->  IAiService / AiService (business prompts)
                                          ->  OpenAiCompatClient (HttpClient, bearer key, timeout)
                                          ->  POST {BaseUrl}/chat/completions
                                          ->  your configured OpenAI-compatible provider
```

- The **browser never talks to the AI provider**: it holds no URL, no API key and no
  model credentials. All AI HTTP happens server-side.
- The provider **Base URL**, **API key** and **Model** are resolved at runtime from the
  effective configuration (server environment merged with per-user Settings overrides
  on the desktop). A trailing slash is normalised, so the request is exactly
  `{BaseUrl}/chat/completions` - never `/v1//chat/completions`.
- Body text is extracted to plain text (HTML stripped) server-side, bounded per message
  and per thread, and clearly marked when truncated.
- Every operation is cancellable (`CancellationToken` end to end) and has its own
  timeout; an unreachable provider returns a clean error instead of hanging the UI.

## AI features

- **Summarize** - concise summary of the open message (subject/sender/date/body context).
- **Summarize thread** - whole conversation (chronological, bodies loaded server-side)
  with sections: Summary / Decisions / Open questions / Action items.
- **Suggest reply** - key points + a short sample answer, shown in the AI panel; the
  user can insert it into the reply box.
- **Generate reply** - complete plain-text draft **inserted into the editable reply
  box**. AI generates text only.

### AI never sends email

This is a hard guarantee. AI operations only ever return text. The **only** path that
sends a reply is the existing `POST /api/messages/{itemId}/reply`, invoked by the
user pressing **Send reply** after reviewing/editing the text. No code path exists that
sends an AI-generated draft automatically.

## AI status

- `GET /health/ai` returns HTTP 200 in every state with `status` = `connected` |
  `not_configured` | `authentication_failed` | `unavailable`. The probe is a lightweight
  `GET {BaseUrl}/models` - never a real prompt - and the outer `/health` stays healthy
  when the provider is down. Providers that only expose `/chat/completions` are probed
  through the Settings **Test connection** button, which reports the same sanitized
  states.
- Header pill shows **AI connected / AI: not configured / AI: auth failed /
  AI unavailable**.
- If AI is not configured, the AI buttons surface **"AI is not configured"** instead of
  crashing; the email client keeps working normally. The mail page additionally shows a
  non-blocking banner with an **Open Settings** button when the desktop store holds no
  API key yet, so setup is one click away and never blocks reading mail.


## Security notes

- On Windows desktop the AI key is stored per-user in the **Windows Credential Manager**
  (target `EmailAI/AI`, scoped to the current user) from the Settings page; on
  server/container deployments the key stays in the environment (`AI_API_KEY`, with
  `AI_CREDENTIAL_SOURCE=environment` making it the only source). Nothing secret ships
  in appsettings.json or the installer.
- The non-secret values a desktop user saves in Settings (Exchange endpoint/mode/account
  and the AI Base URL/Model) are stored together in ONE per-user JSON document,
  `%APPDATA%\EmailAI\settings.json` - never a secret. A pre-existing
  `%APPDATA%\EmailAI\ai-settings.json` (the earlier AI-only file) is migrated into it once,
  on first read, and is left in place.
- The Exchange password is stored per-user in the Windows Credential Manager (target
  `EmailAI/Exchange`) and is read only when the saved mode is `UsernamePassword`; switching
  to `Windows` mode deletes it. The two secrets have separate targets, so one can never be
  read, overwritten or deleted by accident when the other is managed.
- The key is resolved at runtime and sent only as the `Authorization` header of the
  server-to-provider request. It is never logged, never written to disk by the app, never
  returned by any endpoint (GET `/api/settings/ai` and `/health/*` report presence and
  sanitized state only) and never displayed back in the Settings UI.
- Failure logs contain status code, endpoint path and model name - never request/response
  bodies (emails can be sensitive) and never the API key.
- The Windows Credential Manager entry is scoped to the current Windows user: other
  accounts on the same machine cannot read or remove it.
- `desktop/scripts/verify-secrets.js` scans the publish and packaged output before a
  release and fails the build when any `Password`/`ApiKey`-style assignment carries a
  value or a known retired secret is found verbatim.
- Email content is treated as **untrusted data**: the system prompt forbids following
  instructions found inside emails (prompt-injection hardening), inventing facts, and
  claiming actions were taken. Email content is never logged and is only ever sent to
  the provider you configured.
- HTML message bodies are rendered in a sandboxed `<iframe>` (`srcdoc`, no
  `allow-scripts`, restrictive `Content-Security-Policy`) and additionally sanitised:
  scripts, nested frames, forms, inline event handlers and dangerous URL schemes
  (`javascript:`, `vbscript:`, `data:text/html`, obfuscated spellings) are removed, and the
  filter runs until it is stable so nesting tricks such as `<scr<script>ipt>` cannot
  re-form a tag. Links open in a new window with `rel="noopener noreferrer"`. The plain
  text body remains available as a toggle and as the fallback.
- The new-mail notification endpoint returns **headers only** (sender, subject, receive
  time) and the app's own de-duplication state is per process, so a restart is quiet and a
  message is never announced twice.

## Tests

`tests/EmailAI.Tests` (xUnit, no live LLM/AI provider, fake `HttpMessageHandler`s):

1. Missing AI configuration (client throws `ai_not_configured` without touching HTTP).
2. Endpoint construction (no doubled path, trailing-slash normalisation, root/API-root
   shapes) via `AiEndpointBuilderTests`.
3. Model selection (configured default vs request override).
4. Authorization header handling (present when key set, absent otherwise; the key always
   comes from the credential provider, never stale options).
5. Successful chat-completion parsing.
6. Non-success HTTP mapping (401/403, 429, 5xx) and probe classification
   (`authentication_failed`, `unavailable`).
7. Timeout and caller-cancellation semantics.
8. Empty/malformed response handling.
9. Prompt-injection wording in all four prompts.
10. Per-message and whole-thread truncation behaviour.
Plus HTML-to-text extraction, `AiService` prompt wiring, host tests (`/health` healthy +
`/health/ai` = `not_configured`) via `WebApplicationFactory`, credential/configuration
policy unit tests (`AiCredentialCoordinatorTests`: auto/windows/environment precedence,
per-user Base URL/Model override + clear semantics, validation, environment save/delete
rejection), and full Settings host tests (`AiSettingsEndpointsTests`: the API key is
stored through the store abstraction, never echoed by GET, empty/invalid requests are
rejected, per-user provider overrides persist and clear, the test connection reports
not_configured/unavailable/authentication_failed without secrets, and
`/api/settings/exchange` returns the explicit Exchange mode (`Windows` / `UsernamePassword`), the detected Windows identity and credential-presence booleans - never credentials).

Exchange authentication unit tests (`ExchangeAuthModeTests` / `ExchangeConfigurationTests`) cover the two explicit modes (Windows and UsernamePassword) with fake EWS service objects: each mode uses only its own credentials, a failed Windows attempt never falls back to username/password (and vice versa), configuration validation requires username+password only in `UsernamePassword` mode, invalid modes are rejected, and passwords never appear in exceptions, logs, health text or API responses.

Per-user configuration tests cover the whole persistence and precedence policy offline:

- `FileUserSettingsStoreTests` - round-trips of `%APPDATA%\EmailAI\settings.json` in a temp
  directory, tolerance for a missing/empty/corrupt file (a corrupt file is quarantined once
  as `settings.json.corrupt`), the one-time `ai-settings.json` migration, and the guarantee
  that only non-secret fields are ever written.
- `ExchangeConfigurationProviderTests` - a complete per-user configuration wins as a whole,
  the deployment snapshot is used unchanged otherwise, the per-user password is read only
  from the `EmailAI/Exchange` target, deployment credentials are never mixed in, and a
  credential-store failure becomes a typed configuration error.
- `ExchangeSettingsServiceTests` - validation before any write (unsupported mode, missing
  account/password, invalid or placeholder endpoint), Windows mode deleting a stored
  password, secrets reaching only their own target, test drafts that are never persisted,
  and DELETE clearing the document and the secret while keeping the AI section.
- `ExchangeSettingsEndpointsTests` - the real host with in-memory stores: GET never returns
  the password (nor a deployment-managed account), PUT writes the document/secret halves
  correctly, invalid drafts answer 400 without side effects, POST `/test` probes a draft
  without persisting it, and AI/Exchange secrets never disturb each other.

The features added on top of that slice are covered by dedicated suites:

- `MailboxIdentityTests` - identity normalisation (blank values, `Name <address>` forms,
  unknown source), alias derivation from authoritative values only (most specific first,
  capped, case-insensitively deduplicated, single-character noise dropped) and the secret-free
  `Describe`.
- `AiPromptsTests` / `AiServiceTests` - every operation injects the authoritative current
  user into the system prompt (outside the untrusted-data fences, with aliases), an unknown
  identity tells the model not to guess, and an identity claimed *inside* an email stays
  data.
- `MailNotificationServiceTests` - baseline on the first poll, only new mail afterwards,
  no duplicates on re-poll, per-folder baselines and folder switching, oldest-first ordering,
  timestamp-less arrivals, and the header-only shape.
- `NotificationEndpointsTests` - the same contract through the real host: the JSON envelope
  and every item carry exactly the documented members, folder query handling, one header
  query per poll, and no secret-ish fields.
- `MailHtmlSanitizerTests` - script/embed/form removal, inline event handlers, dangerous and
  obfuscated URL schemes, comments, `data:image` kept but `data:text/html` refused, link
  hardening, formatting preserved, the CSP-bearing sandbox document, and the stable-result
  behaviour that defeats `<scr<script>ipt>`-style nesting tricks.
- `AiTextDirectionTests` - explicit Persian/English choices, Auto detection for Persian,
  Arabic, Hebrew, Greek and Cyrillic, dominance in mixed text, digits/punctuation never
  outvoting Persian, and the blank fallback.
- `AiReadinessServiceTests` / `AiReadinessEndpointTests` - every readiness state (not
  configured, missing key, missing model, invalid URL, ready with/without a probe, probe
  failures, credential-store failure, unexpected failure) with a user-facing message and
  action, plus the HTTP contract (always 200, actionable payload, no key).
- `AppStatusNotifierTests` - fan-out to every subscriber, area forwarding, revision counter,
  unsubscribe, and a throwing subscriber never breaking the writer or the others.
- `InitialInboxLoadTests` - the root page is prerendered **with the inbox messages already
  present** (no empty/loading state), queries only the inbox, once, and shows the empty
  state rather than an error for an empty mailbox.

Opt-in **real** integration tests (`AiIntegrationTests`, `ExchangeIntegrationTests`) are
disabled by default; when the integration environment is absent they report
"not configured" and complete without calling any service (never using fake
credentials). When the environment is present they run for real:

- AI: `EMAILAI_AI_INTEGRATION_TEST=true` plus `EMAILAI_AI_BASE_URL`, `EMAILAI_AI_API_KEY`
  and `EMAILAI_AI_MODEL` (any OpenAI-compatible endpoint). The test issues one real
  chat completion and asserts a valid non-empty response; the key is never printed.
- Exchange: `EMAILAI_EXCHANGE_INTEGRATION_TEST=true` plus the `EXCHANGE_*` variables
  (`EXCHANGE_AUTHENTICATION=Windows` or `UsernamePassword`). The test runs a real EWS
  root-folder probe through the configured single mode.
Never commit real credentials.

## Design notes

- **Interactive Server rendering** (Blazor Web App) - one process, the intended shape
  for the later Docker/Electron packaging phases.
- Repository has **no React/Vue/Angular/Vite/npm** anywhere - the UI is C# and Razor.
- AI content is never persisted (no database, no Entity Framework).

## Current limitations

- Requires Exchange to show mail: either a per-user configuration saved in Settings
  (endpoint + mode + account/password) or the deployment's `EXCHANGE_EWS_URL` - plus
  `EXCHANGE_DOMAIN`/`EXCHANGE_USERNAME`/`EXCHANGE_PASSWORD` in `UsernamePassword` mode
  (`Windows` mode uses only the current Windows identity). Authentication is explicit -
  there is no automatic NTLM fallback.
  AI requires a provider (configured through Settings on the desktop or
  `AI_BASE_URL`/`AI_MODEL` on servers). Without them the UI reports the exact missing
  state.
- No message marking (read/unread), move, delete or compose-new yet - read + reply slice.
- Email content is sent to the configured AI provider; do not point `AI_BASE_URL` at a
  provider you do not trust with your mail.
- Draft quality depends on the configured model; truncation keeps inputs bounded but
  very long threads only receive a representative slice of each message.
- Attachment **download** (saving an attachment to disk) remains future work; the list
  shows attachment metadata only.

## Intentionally NOT implemented yet

- **Docker** (Dockerfile / compose) - later phase; this single process is container-ready.
- **Electron desktop shell** - implemented under `desktop/`: `npm run release` runs the
  .NET test suite, republishes the self-contained backend, packages the Windows NSIS
  installer (`EmailAI-Setup-<version>.exe`) and secret-scans the result. The shell also
  polls `GET /api/notifications/mail?folder=inbox` every 30 s and raises one **native
  Windows notification** per new message (sender as the title, subject as the body);
  clicking it focuses the window and opens that exact message through the
  `/?item=<exchange item id>` deep link. The backend does the de-duplication, the first
  poll only establishes the baseline (a fresh launch never notifies for old mail), and
  the feature is disabled on non-Windows hosts or with
  `EMAILAI_DISABLE_NOTIFICATIONS=1`.
- **Database / AI persistence, IMAP, Microsoft Graph, automatic email sending** - all
  out of scope by design.

