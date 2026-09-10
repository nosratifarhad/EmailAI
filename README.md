# EmailAI

**AI-assisted email for Microsoft Exchange, delivered as a Windows desktop application.**

EmailAI is a Windows mail client for **Microsoft Exchange (EWS)** with a built-in **AI
assistant** that summarizes messages and conversations and drafts replies. It ships as one
application: an **Electron shell** starts a bundled, self-contained **ASP.NET Core (.NET 10)
backend with a Blazor Web App UI** on `127.0.0.1` and shows that UI in a native window. Your mail
is read through **your own Exchange account**, and AI requests go straight from your machine to
**the OpenAI-compatible provider you configured** - there is no EmailAI cloud service and no
gateway in the middle.

---

## Download for Windows

**Normal users do not need to build EmailAI from source.** No .NET SDK, no Node.js, no clone.

1. Open the **Releases** page of this repository and download the latest
   `EmailAI-Setup-<version>.exe` (for example `EmailAI-Setup-1.1.0.exe`) - optionally with its
   `.sha256` checksum file next to it.
2. Run the installer (per-user install, **no administrator rights required**). Windows
   SmartScreen may warn because the build is not code-signed: *More info → Run anyway*.
3. Launch **EmailAI** from the Start menu or the desktop shortcut.
4. Open **Settings** (the gear button in the header) and configure **Exchange** and your
   **AI provider** - both cards test the connection before you save (see [First run](#first-run)
   and [Configure Exchange](#configure-exchange)).
5. Read your Inbox, then use **Summarize**, **Suggest reply** and **Generate reply**.

> **Where the release lives.** The installer is published as a **GitHub Release asset** of this
> repository (`Releases` → the version you want → `Assets`), together with `EmailAI-Setup-<version>.exe.sha256`.
> This working copy has **no Git remote configured**, so no absolute URL can be stated here without
> inventing one. The release notes for the current version are in
> [`RELEASE-NOTES.md`](RELEASE-NOTES.md). Building from source is described under
> [Developer setup](#developer-setup) - it is **not** the normal install path.

**End users** → download the installer above: no Git, no .NET SDK, no Node.js, no clone, no build.
**Developers** → [clone and build](#developer-setup).

The local backend serves this surface (one origin - no CORS, no second server):

```text
GET    /api/folders/{folder}/messages            message list (paged)
GET    /api/folders/{folder}/children            the folders nested below a folder (custom folders)
GET    /api/messages/{itemId}                    full message
GET    /api/messages/{itemId}/thread             conversation
POST   /api/messages/{itemId}/reply              send reply (EWS) - the ONLY sender
GET    /api/ai/readiness                         can the assistant be used right now?
POST   /api/ai/messages/{itemId}/...             AI operations (server-side)
GET    /api/notifications/mail                   new-mail feed for the desktop shell
GET/PUT/DELETE /api/settings/ai|exchange         per-user configuration (+ POST .../test)
GET    /health | /health/exchange | /health/ai   status probes
```

```text
Windows desktop
+---------------------------------------------------------------------------+
| Electron shell (desktop/main.js)                                          |
|   - starts resources/server/EmailAI.Api.exe on a free 127.0.0.1 port       |
|   - waits for /health, then loads the Blazor UI in the window              |
|   - polls /api/notifications/mail every 30 s -> native Windows notification|
+---------------------------------------------------------------------------+
        |  http://127.0.0.1:<port>   (loopback only - never exposed to the network)
+---------------------------------------------------------------------------+
| EmailAI.Api (ASP.NET Core, self-contained win-x64)                        |
|   - Blazor Web App UI (Interactive Server) + REST API on one origin        |
|   - Application layer: AI prompts/services, HTML sanitiser, notifications   |
|   - Infrastructure: EWS (Exchange) client, OpenAI-compatible HTTP client    |
|   - Secrets: Windows Credential Manager (per Windows user)                 |
+---------------------------------------------------------------------------+
        | EWS/SOAP over your corporate network      | HTTPS to your provider
        v                                           v
   Microsoft Exchange                        OpenAI-compatible AI API
   (the mailbox you are signed in to)        (Base URL + key you configured)
```

Everything in those boxes is **local to your machine**. Only two things leave it: mail is read
from **your** Exchange server, and the text EmailAI sends to your **configured AI provider** for
an AI action you explicitly triggered.

## Who it is for

* People who work in **Microsoft Exchange / Outlook Web App** environments and want an AI
  assistant that understands **who they are** (the mailbox owner) instead of guessing.
* Teams that must use a **specific, company-approved OpenAI-compatible endpoint** rather than a
  fixed public vendor.
* Persian/English speaking users, because EmailAI drafts replies in the language you pick and
  lays them out in the correct reading direction (RTL/LTR).

## Features

| Feature | What it does |
| --- | --- |
| Exchange mail | Folder list, paged message list, message detail (From/To/Cc/Bcc, date, attachments), conversation view, reply/reply-all sent through EWS |
| Current-user identity | The AI is told who it is helping - the mailbox owner resolved from the Exchange account and directory, never inferred from the email text |
| AI assistant | Summarize message, summarize thread, suggest reply, generate an editable reply draft |
| Suggested replies | Shown in the AI panel and insertable into the reply box; nothing is ever sent automatically |
| Persian/English direction | Pick the response language: Persian renders RTL/right-aligned, English LTR/left-aligned; the explicit choice wins over the browser language |
| HTML email rendering | Renders the message's HTML body, with a toggle back to the plain-text extraction |
| HTML security | Untrusted mail HTML is sanitized and shown in a sandboxed, script-less iframe with a restrictive CSP - no script, frame, form or dangerous URL survives |
| AI readiness | States *before* you try whether the assistant is usable ("not configured", "no key yet", "endpoint unreachable", "key rejected", "rate limited", "timeout") and offers the Settings path that fixes it |
| Windows notifications | One native notification per newly arrived message (sender + subject only), never for mail that already existed when the app started |
| Notification deduplication | De-duplication happens server-side, so re-rendering, re-polling or switching folders can never produce duplicates |
| Notification click / deep link | Clicking a notification focuses the window and opens that exact message (`/?item=<id>`) |
| Settings synchronization | Saving AI or Exchange settings updates the header status and the mail view **immediately** - no reload, no restart |
| Initial Inbox | The first paint already contains the Inbox (fetched during prerendering and handed to the interactive page), with one bounded retry if the very first Exchange call hiccups |

Per-area contracts, invariants, failure modes and the implementing files live in the
specification set: [`.ai/spec/`](.ai/spec/README.md).

## Requirements

| Requirement | Details |
| --- | --- |
| Operating system | **Windows 10 / Windows 11, x64** (the desktop shell, native notifications and the Credential Manager are Windows-specific) |
| .NET runtime | **Not required** for the desktop app: the backend is published **self-contained** (win-x64) and bundled in the installer. The .NET 10 SDK is needed only to build from source |
| Node.js | **Not required** for end users. Node/npm are only needed to build the Electron shell or package a release |
| Exchange | A reachable **EWS endpoint** (`https://mail.contoso.com/EWS/Exchange.asmx`), Exchange 2013 SP1 or later. Either the current **Windows account** authenticates (Windows Integrated Authentication) or you are given an explicit **username/password** account |
| AI provider | Any service exposing an **OpenAI-compatible Chat Completions API**: a Base URL (its API root, e.g. `https://host/v1`), a model identifier and - unless the endpoint is unauthenticated - an API key |
| Network | Outbound HTTPS to your Exchange server **and** to your AI provider. EmailAI listens only on `127.0.0.1` (loopback) and never opens an inbound port to the network |
| Disk | ~1 GB installed (the bundled self-contained backend dominates the size) |

Nothing else is required: no database, no IIS, no certificate, no cloud account - and no data
other than what is described above leaves your machine.

## Install, upgrade and uninstall (Windows)

| Step | What happens |
| --- | --- |
| Installer type | **NSIS installer**, `EmailAI-Setup-<version>.exe`, **per-user** (`perMachine: false`) - no administrator rights, no UAC prompt |
| Install location | `%LOCALAPPDATA%\Programs\EmailAI` by default; the assisted installer lets you choose another folder |
| Shortcuts | Start-menu entry and desktop shortcut named **EmailAI** (both optional in the installer UI) |
| First launch | The shell starts the bundled backend, waits for `/health` (typically < 1 s), then shows the UI. Configure Exchange and AI in **Settings** - nothing else is required |
| Windows notifications | Native toasts need Windows notifications enabled for the app (Settings → System → Notifications). Nothing is ever shown for mail that already existed when the app started |
| Upgrade | Run a newer `EmailAI-Setup-<version>.exe`: it upgrades in place. Your configuration is **kept** - it lives in `%APPDATA%\EmailAI\settings.json` and the Windows Credential Manager, not in the install folder |
| Uninstall | **Settings → Apps → Installed apps → EmailAI → Uninstall**, or `"%LOCALAPPDATA%\Programs\EmailAI\Uninstall EmailAI.exe"`. Saved settings and stored secrets are not removed by the uninstaller; delete `%APPDATA%\EmailAI` and the `EmailAI/AI` + `EmailAI/Exchange` credentials in Credential Manager to remove them too |
| Data left behind | `%APPDATA%\EmailAI` (settings document, logs, the Electron shell's cache) - no mail content is ever stored there |

The app is **not code-signed** (no certificate is available for this project), so Windows
SmartScreen may show an "unknown publisher" prompt on first launch: choose *More info* → *Run
anyway* to continue.

## First run

EmailAI starts with **no configuration at all**: nothing is preconfigured, nothing is guessed and no
credential ships with the installer. The window opens on the mail view with two things visible - the
header status pills and, for each integration that is missing, an actionable message instead of an
error.

1. **The header shows the state of each integration** - `App`, `Exchange` and `AI`. On a fresh
   installation the last two read *not configured*. That is expected, and the application never
   crashes or hangs because of it.
2. **Open Settings** (the gear button in the header, route `/settings`). Both cards validate what
   you type and can test the connection against the real service before anything is stored.
3. **Configure Exchange first** - it is what makes your mailbox appear
   ([Configure Exchange](#configure-exchange)).
4. **Configure your AI provider** - deliberately independent of Exchange: either integration can be
   configured without the other ([Configure the AI provider](#configure-the-ai-provider)).
5. **Return to the mail page.** The Inbox is already there on the first paint (not an empty shell),
   and the AI actions stay disabled until the assistant is ready - the banner says exactly what is
   missing ([AI readiness](#ai-readiness)).

Nothing else is required: no database, no sign-up, no cloud account, no license server. Everything
you configure is stored per Windows user (see [Storage and security](#storage-and-security)).

## Configure Exchange

Everything is done in **Settings → Exchange connection**; no environment variable is required for
the desktop application.

| Field | What to enter |
| --- | --- |
| **EWS URL** | Your Exchange Web Services endpoint, e.g. `https://mail.contoso.com/EWS/Exchange.asmx`. It must be an absolute `http(s)` URL - anything else is rejected before it is saved |
| **Authentication** | `Windows` (the current Windows account) or `Username + Password` (an explicit account) - see below |
| **Account / domain** | Only in `Username + Password` mode. The domain is optional |
| **Password** | Only in `Username + Password` mode; a masked, write-only field |

**Windows authentication.** EmailAI authenticates with the **current Windows account only**
(`UseDefaultCredentials`). You type no password and none is stored. The card shows the account that
was detected (domain, user, full name and how it was detected) so you can see which identity will be
used before you save.

**Username + Password authentication.** EmailAI authenticates with the **explicit account you type**.
Username and password are both required; the Windows identity is never used in this mode.

There is **no silent fallback in either direction**: a rejected Windows account is reported as an
authentication failure and a rejected password is reported as an authentication failure. EmailAI
never retries a failed attempt with the other mechanism - the mode you selected is the mode that is
used. Fix the mechanism that failed.

**Where the credentials go.** The non-secret values (endpoint, mode, account, domain) are stored in
`%APPDATA%\EmailAI\settings.json` for your Windows account; the password is stored in the **Windows
Credential Manager** (target `EmailAI/Exchange`). It is never written to a JSON file, never logged
and never returned by an endpoint - the settings API reports booleans (`usernameConfigured`,
`passwordConfigured`, `hasPassword`) only.

**Buttons.** *Test connection* probes exactly what is in the form, through the selected mode,
**without saving anything**. *Save Exchange settings* validates first and stores only after that (a
rejected save has no side effects; switching to `Windows` mode deletes a previously stored Exchange
password). *Remove saved settings* deletes the per-user configuration and its credential, returning
your Windows account to the deployment configuration.

**If Exchange is not configured** the application still starts and stays usable:
`/health/exchange` and the header pill report *not configured*, and every mail endpoint answers with
an actionable error (*"Exchange is not configured"*). The sample endpoint in `appsettings.json`
(`mail.example.com`, a reserved documentation host) is recognised as a placeholder, reported as "not
configured" and never contacted. A malformed EWS URL is rejected by validation before it is saved; an
unreachable one surfaces as a connection error with a *Try again* action - see
[Troubleshooting](#troubleshooting).

## Configure the AI provider

EmailAI works with **any service that exposes an OpenAI-compatible Chat Completions API** - an
external provider, a company gateway or a local model server. Nothing is tied to a vendor: there is
no hard-coded provider, base URL or model name in the code, and EmailAI is not a proxy - a request
goes from your machine straight to the endpoint you configured.

Configure it in **Settings → AI provider**:

| Field | What to enter |
| --- | --- |
| **Base URL** | The provider's API root, e.g. `https://api.openai.com/v1`, `https://ai.your-company.example.com/v1`, or `http://127.0.0.1:8080/v1` for a local server. A trailing slash is fine |
| **API key** | Sent as `Authorization: Bearer <key>`. Leave it empty only when your endpoint needs no authentication |
| **Model** | The model identifier exactly as your provider expects it (for example `gpt-5`, or your gateway's model name) |

The calls EmailAI makes are the standard OpenAI-compatible ones:

```text
POST {BaseUrl}/chat/completions     the AI operations (summarize, suggest reply, generate draft)
GET  {BaseUrl}/models               the lightweight readiness probe behind the status pill
```

The path is normalised, so `https://host/v1/` and `https://host/v1` both produce
`https://host/v1/chat/completions` - never `/v1/v1/...` and never a doubled `/chat/completions`. If
your provider does not expose `/models`, the pill simply cannot probe it; use **Test connection**,
which performs a real request through the same client and reports the same sanitized states.

**Where the values live.** Base URL and model are non-secret and are stored in
`%APPDATA%\EmailAI\settings.json`; the API key is stored in the **Windows Credential Manager**
(target `EmailAI/AI`), scoped to your Windows account. The key is never displayed back, never
logged, never returned by an endpoint and never sent to the browser - all provider traffic happens
inside the backend process. Server and container deployments can supply the same three values
through the environment instead (`AI_BASE_URL`, `AI_MODEL`, `AI_API_KEY`), with
`AI_CREDENTIAL_SOURCE` deciding which source is authoritative - see
[Configuration](#configuration) and [Storage and security](#storage-and-security).

## Notifications (new mail on the desktop)

* **Feed, not push:** Exchange gives this application no push channel, so the Electron main
  process polls `GET /api/notifications/mail?folder=inbox` every **30 s**. That is public,
  intentional behaviour of the desktop shell (browser/server deployments never call it).
* **Silent baseline:** the first poll of a session only records what the mailbox already
  contains, so launching EmailAI never produces a toast for existing mail.
* **De-duplication:** the *backend* tracks the ids it has already reported
  (`IMailNotificationService`), so re-rendering, re-polling, page reloads or a folder switch can
  never produce a duplicate notification. Only mail that is newer than the mailbox's
  high-water mark is reported, oldest first, and at most five toasts are raised per poll.
* **Payload:** the sender (display name or address) as the title and the **subject** as the
  body. **No email body, no credentials, no tokens, no API keys** - the feed has no such fields.
* **Click behaviour:** clicking a toast restores/focuses the window and opens that exact message
  through the deep link `/?item=<exchange item id>` (an unusable id is ignored rather than
  breaking the mail client).
* **Turning it off:** set `EMAILAI_DISABLE_NOTIFICATIONS=1` for the EmailAI process (the feature
  is also disabled automatically on non-Windows hosts or when the OS does not support
  notifications).

## Repository layout (developers)

| Project | Responsibility |
| --- | --- |
| `src/EmailAI.Api` | Host: REST endpoints, **Blazor Web App UI**, typed UI API client, AI options binding |
| `src/EmailAI.Application` | `IExchangeMailService`, **`IAiService` / `AiService` / `AiPrompts`** (business logic + prompts) |
| `src/EmailAI.Domain` | DTOs and option types shared by every layer (`Mail`, `Exchange`, **`AI`**) |
| `src/EmailAI.Infrastructure` | **EWS implementation** and **`OpenAiCompatClient`** (OpenAI-compatible HTTP) |
| `tests/EmailAI.Tests` | xUnit tests for the AI layer, the mail/identity/notification surface and the host endpoints (fake HTTP handlers and in-memory Exchange - no live provider or mailbox) |
| `tools/EwsApiProbe` | Reflection probe used during EWS development |
| `tools/EwsAuthProbe` | Reproducible EWS authentication probes (explicit Windows / UsernamePassword modes) |
| `.github/workflows` | `ci.yml` verifies every pull request and every push to `main` (build, tests, shell syntax, secret/configuration scan); `release.yml` packages the Windows installer for a `v*.*.*` tag |

## Run from source (developers)

End users install the packaged app (see [Download for Windows](#download-for-windows)); the
commands below are the developer loop.

```bash
dotnet build EmailAI.slnx          # must finish with 0 warnings / 0 errors
dotnet test tests/EmailAI.Tests    # AI unit + host tests (no live provider needed)
# Real integration tests are opt-in and run only when their environment is present (see "Testing").
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

### Storage and security

| Data | Storage |
| --- | --- |
| AI API key | **Windows Credential Manager** - target `EmailAI/AI`, scoped to the current Windows user |
| Exchange password | **Windows Credential Manager** - target `EmailAI/Exchange`, scoped to the current Windows user |
| AI non-secret settings (Base URL, model) | `%APPDATA%\EmailAI\settings.json` |
| Exchange non-secret settings (EWS URL, mode, account, domain) | `%APPDATA%\EmailAI\settings.json` |

Each secret has its **own** target, so one can never be read, overwritten or deleted by accident
while the other is being managed. Credential Manager entries are protected at rest by Windows (the
per-user credential vault, DPAPI) and are readable only by that Windows account; EmailAI does not add
a second encryption layer of its own and does not claim one.

**Never stored in `settings.json`, appsettings, logs, API responses, the installer, or files next to
the application:** the Exchange password, the AI API key, any bearer token, and email content. The
settings document holds only the four non-secret values above:

```json
{
  "exchange": { "ewsUrl": "https://mail.contoso.com/EWS/Exchange.asmx", "authentication": "Windows" },
  "ai": { "baseUrl": "https://ai.your-company.example.com/v1", "model": "gpt-5" }
}
```

A missing, empty or corrupt settings file never breaks the application: an unreadable document is
moved aside once as `settings.json.corrupt` and EmailAI continues with the deployment configuration,
so the mail client keeps working (see [Troubleshooting](#troubleshooting)).

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
| Custom folder tree (expanding Inbox) | `GET /api/folders/{folder}/children` |
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

## Email rendering

- **HTML bodies are preferred over the plain text**: an opened message with
  `bodyHtml` renders that HTML, and a toggle switches back to the extracted plain text.
  The HTML is sanitised and shown inside a sandboxed, script-less `<iframe>` with a
  restrictive CSP (`MailHtmlSanitizer`), so mail can never script the app or the session
  (`srcdoc`, no `allow-scripts`). A message with no HTML falls back to the plain text,
  and a message with no body at all renders an explicit "no body" state - never a blank
  pane.
## Response language and direction (RTL/LTR)

- **Writing direction follows the content**: AI output and the reply composer carry
  `dir="rtl"`/`dir="ltr"` (and matching alignment) decided by `AiTextDirectionResolver` -
  an explicit Persian/English choice wins, `Auto` follows the actual text
  (Persian/Arabic/Hebrew are right-to-left). No component hardcodes "Persian means right".
## AI readiness

- **AI readiness is stated before it is attempted**: the mail page and the AI panel show
  a non-blocking banner from `GET /api/ai/readiness` ("not configured", "no API key
  saved yet", "endpoint unreachable", "key rejected", "rate limited", "timeout", ...)
  with an **Open Settings** action, and the AI buttons are disabled while it is not
  ready. A failed operation is mapped through the very same user-message table, so the
  user never sees a raw HTTP status or an exception message.
## Live status refresh

- **The header status updates immediately**: saving AI or Exchange settings in the
  Settings page publishes an `AppStatusNotifier` change, so the header pills re-check
  right away (they still poll every 20 s as a safety net), and an Exchange change
  re-lists the folder instead of leaving stale mail on screen.
## Initial Inbox

- **The first paint contains the Inbox**: the initial folder load runs in the component
  initialization pipeline, so it happens during prerendering; the page is carried into
  the interactive circuit through `PersistentComponentState`, which means exactly one
  Exchange query and no empty first render. A **transient connection failure** (the very
  first EWS call of a session can be slow or refused) gets exactly **one bounded retry**
  before the list reports an error - no delay, no credential change, and a second failure
  still reaches the user unchanged.

## Custom mail folders (Outlook-style folder hierarchy)

- **The sidebar shows the folders your mailbox really has.** Inbox, Sent, Deleted (and the other
  well-known folders) are the fixed top level; the folders you created in Outlook are nested under
  Inbox and are read from Exchange - nothing is hard-coded and no sample folder is ever invented.
- **Expanding Inbox is a separate action from selecting it.** The arrow expands/collapses the
  children and never changes the selected folder; clicking *Inbox* still loads the Inbox exactly as
  before, and clicking a child folder loads only that folder's messages with the same paging,
  sorting, empty state, refresh and error handling as every other folder.
- **Nothing is fetched until you ask.** The first paint performs exactly one Exchange query (the
  Inbox page); the folder walk runs the first time Inbox is expanded and its result is remembered for
  the session. Re-expanding, collapsing and switching between the discovered folders never query
  Exchange for the folder list again.
- **A folder is identified by its Exchange folder id, not its name** (`folder:<hex>` REST key), so two
  folders with the same name stay distinguishable and renaming a folder in Outlook never changes which
  one is selected. The key is an opaque folder id, never a credential; the UI holds no Exchange
  account and no secret.
- **A folder that cannot be read never breaks the mail client.** A failed folder walk is reported
  inside the folder pane with a retry action while Inbox/Sent/Deleted keep working; a selected folder
  that no longer exists answers `404 folder_not_found` and the list shows its normal error state.
- If a folder has no subfolders, the arrow disappears after the first expansion (the folder is simply
  empty). A folder without a name is shown as `(unnamed folder)` instead of a blank row.

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
  Alex to approve this?" is understood as "the current user". The model is also told to
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

## Suggested replies

**Suggest reply** analyses the message you have open (for a thread: the whole conversation) together
with **who you are**, and returns the key points that need answering plus a short sample answer.
**Generate reply** instead produces a complete plain-text draft and inserts it into the editable
reply box, where you can rewrite it before sending.

* **The AI is told who it is helping.** Every AI operation receives the **authoritative current
  user** ([Current-user identity](#current-user-identity-who-the-ai-is-told-it-is-helping)): the
  mailbox owner resolved from the Exchange account and the Exchange directory - display name, SMTP
  address and account. The identity is delivered as **trusted context outside the untrusted email
  data**, aliases are derived only from those authoritative values, and the model is told to follow
  that identity even when the email text claims otherwise.
* **The identity is never guessed.** It is never inferred from the email body, a signature block or
  the recipient list. When no identity can be resolved, the prompt says so explicitly and forbids
  inventing a name or a signature.
* **The language you choose decides the direction.** Every AI action takes the response language:
  **Persian** is rendered right-to-left and right-aligned, **English** left-to-right and
  left-aligned, and **Auto** follows the language of the produced text. An explicit choice always
  wins over the browser language
  ([Response language and direction](#response-language-and-direction-rtlltr)).
* **Nothing is ever sent for you.** A suggestion stays text on the screen until *you* press **Send
  reply**; there is no automatic-send path and no background drafting.

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

## Testing

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
  state rather than an error for an empty mailbox. Two further cases prove the resilience
  behaviour: one transient connection failure is absorbed by a single retry (the first paint
  still carries the Inbox, two attempts), while two failures surface the real error.
- `CustomFolderKeyTests` - the identity of a custom Exchange folder: the `folder:<hex>` key
  round-trips any folder id exactly, carries no URL-hostile character, is case-insensitively
  distinct for distinct ids (so two folders can never collapse into one cache entry), and
  rejects every malformed key (a well-known key, an empty/odd/invalid payload).
- `MailFolderEndpointsTests` - custom folders through the real host: `GET
  /api/folders/inbox/children` reports identity/name/parent/type/hasChildren, only the direct
  children of the requested parent are returned (a grandchild is not a child of Inbox), a custom
  folder key lists exactly that folder's messages after the URL round-trip, an empty custom folder
  is an empty list (not an error), and every failure is typed and secret-free (`400 bad_request`,
  `404 folder_not_found`, `502 exchange_authentication_failed`, `503 exchange_connection_failed`).
- `MailFolderSidebarTests` - the sidebar state that the component renders: Inbox is the only
  expandable row, expanding indents the custom folders directly below it, expanding does not change
  the selection, collapsing hides the children but keeps the selection, selecting a child selects it
  (and opens its parent), two same-named folders stay distinguishable, an empty folder loses its
  arrow, a failed discovery keeps every top-level folder usable with a retry, and an Exchange
  configuration change resets the discovered hierarchy.
- `CustomMailFolderUiTests` - the first paint renders the folder hierarchy control while performing
  **no folder walk** (one Inbox query, no children query, no custom folder in the HTML), keeps the
  top-level folder order and gives only Inbox an arrow, still shows the Inbox when the folder walk
  would fail, and the typed client requests the encoded children endpoint and reads the folders.
- `MailboxIdentityCandidatesTests` - the names EWS `ResolveNames` is asked for: the
  impersonated mailbox first, then the configured account, then the Windows account both
  fully qualified **and** bare (a live directory answers for the bare account name and
  returns nothing for `DOMAIN\user`), with duplicates and an unknown identity handled.
- `AiLanguageRequestTests` / `AiLanguageEndpointTests` - the documented language contract:
  `"English"`/`"Persian"`/`"Auto"` (any casing) and the numeric value both resolve, a missing
  language means Auto, and an unsupported value answers 400 with an actionable message
  (through the real host, with the AI service recording the language and current user it was
  given - and never called at all for an invalid value).
- `ReleasePackagingTests` - the distribution contract, verified against the files that define a
  release: the packaging configuration (artifact name derived from the version, per-user/x64 NSIS,
  bundled self-contained backend, no absolute paths), the lock-file/version agreement, the pipeline's
  single-sourcing and gates, the Release-only exclusion of `appsettings.Development.json`, the shipped
  `appsettings.json` and `.env.example` samples (placeholders only - no secret, no real endpoint), the
  CI workflow, the ignore rules, the README/release-notes artifact name and storage locations, the
  absence of private endpoints and corporate account domains anywhere in the repository, and the real
  scan gate executed against a planted secret, a planted development configuration and the shipped
  sample.

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

## Developer setup

**Prerequisites (development only - an end user needs none of this):** the .NET 10 SDK and Git for
the application and its tests, plus Node.js 22+ / npm 10+ for the desktop shell and the installer.
The commands below build, test and package from source.

```powershell
git clone <your-clone-url>                     # your own checkout
cd <checkout>
dotnet restore EmailAI.slnx
dotnet build   EmailAI.slnx -c Release         # must finish 0 warnings / 0 errors
dotnet test    tests/EmailAI.Tests -c Release  # offline unit + host tests
dotnet run --project src/EmailAI.Api           # backend + Blazor UI on http://localhost:8080
```

The desktop shell and the packaging live in `desktop/` (Node 22+, npm 10+):

```powershell
cd desktop
npm ci                       # install Electron + electron-builder (dev dependencies only)
npm run publish:server       # self-contained win-x64 backend into desktop/aspnet-publish
npm start                    # run the Electron shell against ./aspnet-publish
npm run build:server         # dotnet build EmailAI.slnx -c Release
npm run test                 # dotnet test tests/EmailAI.Tests -c Release
npm run verify:secrets       # secret-scan desktop/aspnet-publish
npm run dist                 # electron-builder --win nsis (uses the current aspnet-publish)
npm run release              # the full, fail-fast release pipeline (see below)
```

`npm start` expects `desktop/aspnet-publish/EmailAI.Api.exe`, so run `npm run publish:server`
first. Useful flags: `EMAILAI_SMOKE_QUIT_MS=30000` quits the app after 30 s (a headless smoke
test of the real start/stop path), `EMAILAI_DISABLE_NOTIFICATIONS=1` disables toasts.

## Contributing

**`main` is protected: every change - including the maintainer's - arrives through a pull request
and must pass CI before it can be merged. Direct pushes, force pushes and deletion of `main` are
rejected by the server.** The full policy is in [`CONTRIBUTING.md`](CONTRIBUTING.md).

1. **Fork** the repository (or clone it, if you have write access).
2. **Create a feature branch** off `main`: `git switch -c fix/short-description main`.
3. **Make the change** and add the tests/documentation it needs.
4. **Run the verification locally** - the same commands the required check runs:
   `dotnet build EmailAI.slnx -c Release`, `dotnet test EmailAI.slnx -c Release`,
   `node --check desktop/main.js`, and `cd desktop; node scripts/verify-secrets.js
   --allow-development-config ../src ../.env.example`.
5. **Open a pull request against `main`** and fill in the template (`.github/PULL_REQUEST_TEMPLATE.md`).
6. **CI runs automatically**: [`.github/workflows/ci.yml`](.github/workflows/ci.yml) starts on the
   pull request and reports the check **`Verify pull request`**.
7. **The pull request must satisfy the repository checks** - `Verify pull request` green, the branch
   up to date with `main`, and no unresolved review conversation - otherwise the merge stays blocked.
8. **The maintainer merges with squash.** `main` accepts squash merges only, so the history stays one
   commit per change; the head branch is deleted afterwards.

| Protected on `main` | Enforced by |
| --- | --- |
| Direct push | Ruleset `main - pull request workflow`: *require a pull request before merging* |
| Force push / non-fast-forward update | Ruleset `main - integrity`: *block force pushes* |
| Deleting `main` | Ruleset `main - integrity`: *restrict deletions* |
| Merge method | Squash only (ruleset + repository settings) |
| Required status check | `Verify pull request` (strict: the branch must be up to date) |
| Bypass actors | None - the rules bind the maintainer too |

Required approvals are **0** because a single maintainer cannot approve their own pull request; the
maintainer reviews every pull request by hand before merging. See
[`CONTRIBUTING.md`](CONTRIBUTING.md#4-branch-protection-on-main) for the reasoning and for what to
change when a second maintainer joins.

## Windows release (packaging and distribution)

**Versioning.** `desktop/package.json` `"version"` is the single source of truth
(electron-builder derives the installer name from it): bump it with
`npm version <x.y.z> --no-git-tag-version`, then run the release. Current version: **1.1.0**.

**Build the installer** - one command, fail-fast (no installer is produced if any step fails):

```powershell
cd desktop
npm run release
```

| Step | What it runs |
| --- | --- |
| 1 | Validate the project layout and toolchain |
| 2 | `dotnet test tests/EmailAI.Tests -c Release` |
| 3 | `dotnet build EmailAI.slnx -c Release` |
| 4 | Clean + `dotnet publish src/EmailAI.Api -c Release -r win-x64 --self-contained true` |
| 5 | Clean + `electron-builder --win nsis` |
| 6 | `node scripts/verify-secrets.js` over the publish **and** the packaged backend: secrets, development-only configuration, non-placeholder endpoints |
| 7 | Verify the installer exists, that the packaged backend is the fresh publish, and write `<installer>.sha256` |

**Artifact:** `desktop/dist/EmailAI-Setup-<version>.exe` (**x64 only** - there is no x86/ARM64
build), plus `...exe.blockmap` and `...exe.sha256`. `desktop/dist/` and `desktop/aspnet-publish/` are
build output and are git-ignored: **binary artifacts are never committed** - they belong on the
release page, not in the source tree. The pipeline fails the release if the Release publish or the
packaged backend contains `appsettings.Development.json`, a secret-shaped value or a non-placeholder
Exchange/AI endpoint.

**Checksums** are written by the pipeline itself (`desktop/dist/EmailAI-Setup-<version>.exe.sha256`,
and printed in the release summary). To verify a download:

```powershell
Get-FileHash .\EmailAI-Setup-1.1.0.exe -Algorithm SHA256   # compare with the published .sha256 file
```

**Publishing.** Attach the installer and its checksum to a GitHub Release (tag `v1.1.0`, assets
`EmailAI-Setup-1.1.0.exe` + `EmailAI-Setup-1.1.0.exe.sha256`, notes from
[`RELEASE-NOTES.md`](RELEASE-NOTES.md)). `.github/workflows/release.yml` automates exactly that on a
`v*.*.*` tag push: it runs `npm run release` (the same command as above, so CI and a local release
cannot drift), scans the source tree, verifies the artifact and its checksum, uploads both as
workflow artifacts and attaches them as the release assets - and never prints a secret.

## Architecture

```text
Electron shell (desktop/main.js)          one window, one process tree, no inbound port
        | picks a free loopback port, starts + supervises, polls the new-mail feed
EmailAI.Api (ASP.NET Core, .NET 10)       self-contained win-x64, 127.0.0.1 only
        |- Components/      Blazor Web App UI (Interactive Server) - the only UI
        |- Endpoints/       REST API: mail, AI, settings, notifications, health
        |- Web/             typed UI client + status notifier behind the live header
        |- Application/     AI prompts/services, HTML sanitizer, notification detection
        |- Domain/          DTOs + option types (mail, Exchange, AI, per-user settings)
        |- Infrastructure/  EWS client, OpenAI-compatible client, settings + secret stores
        v
   Exchange (EWS over HTTPS)    +    your OpenAI-compatible provider (HTTPS)
```

The UI is C#/Razor only and is served by the same process that serves the API: one origin, no CORS,
no second web server. The desktop shell adds exactly three things - it starts the bundled backend on
a free loopback port, shows the UI in a native window, and turns the backend's new-mail feed into
native Windows notifications. Per-area contracts, invariants and failure modes are specified in the
specification set ([`.ai/spec/`](.ai/spec/README.md)); the integration-level summary is
[`.ai/service-context.md`](.ai/service-context.md).

## Design notes

- **Interactive Server rendering** (Blazor Web App) - one process; the desktop shell hosts that
  same process, so there is no second server to run.
- The UI is **C# and Razor only** - no React/Vue/Angular/Vite and no front-end npm build. npm
  exists in `desktop/` solely to run Electron and electron-builder for the Windows shell and
  installer.
- AI content is never persisted (no database, no Entity Framework).

## Troubleshooting

| Symptom | Meaning and what to do |
| --- | --- |
| "Exchange is not configured" / header shows **Exchange: not configured** | No per-user settings are saved and the deployment has no real `EXCHANGE_EWS_URL`. Open **Settings → Exchange connection**, enter the real EWS endpoint (for example `https://mail.contoso.com/EWS/Exchange.asmx`) and **Test connection** before saving |
| Exchange **authentication failed** | The configured mode was rejected. `Windows` mode uses only the current Windows account - sign in as an account that may read that mailbox. `UsernamePassword` mode needs the account, domain and password. EmailAI never switches mode by itself, so fix the mode you selected |
| "Exchange connection failed. The server may be unreachable." / "did not respond in time" | The EWS endpoint could not be reached (VPN off, DNS, proxy, firewall, or an overloaded server). The initial Inbox load retries once automatically; if it still fails, check the network and use **Try again** |
| Inbox is empty although you have mail | The selected folder really is empty for that account, or the account cannot see it. Compare with Outlook/OWA for the same account - the app shows an explicit empty state instead of a blank pane |
| "AI is not configured" | No Base URL/Model (or no key yet). Open **Settings → AI provider**: enter the Base URL (for example `https://host/v1`), the model and the API key, then **Test connection** |
| AI: **not configured** although the fields are filled in | The per-user store still holds no API key (**Save AI settings** with the key), or `AI_CREDENTIAL_SOURCE=environment` is set on that machine, which makes the environment the only source |
| AI: **authentication failed** | The key is wrong, expired or not allowed for that model. Correct it in Settings |
| AI: **rate limited** | The provider is throttling: wait and retry, or use another model/key |
| AI: **unavailable** / timeout | The provider is unreachable from this machine (proxy/firewall) or too slow. Check the Base URL and the network, then **Test connection** |
| Provider that only exposes `/chat/completions` | The status pill probes `{BaseUrl}/models`. If your provider has no `/models`, use **Settings → Test connection**, which performs a real request through the same client |
| HTML body does not render as expected | Some mail depends on CSS or features a mail client blocks; switch to **View as text**. Remote images are fetched over the network and a restrictive proxy may block them |
| No notifications appear | Windows notifications must be enabled for the app (Settings → System → Notifications), EmailAI must be running, and the mail must arrive *after* startup (the first poll is a silent baseline). `EMAILAI_DISABLE_NOTIFICATIONS=1` disables them by design |
| Duplicate notifications | Not expected - the backend de-duplicates by item id. Capture `%APPDATA%\EmailAI\main.log` and `server.out.log` if you see it |
| Window is blank / "could not be loaded" | The bundled backend did not start: check `%APPDATA%\EmailAI\main.log` and `server.out.log`. A leftover `EmailAI.Api.exe` can keep the folder busy if the app was killed - end it and relaunch |
| Want a clean slate | **Settings → Remove saved key** (AI) and **Remove saved settings** (Exchange); that returns the app to the deployment configuration |
| "The EWS URL is not valid" / saving is refused | The endpoint must be an absolute `http(s)` URL that points at the Exchange service, e.g. `https://host/EWS/Exchange.asmx`. A documentation placeholder such as `mail.example.com` is refused as a real endpoint - enter your own server |
| The AI pill says **ready** but an operation fails | The pill probes `{BaseUrl}/models` while an operation calls `{BaseUrl}/chat/completions`. A provider that serves one but not the other reaches the service but not the model: check the **model** identifier (it is sent exactly as you typed it) and use **Test connection** |
| "The mailbox could not be opened" / mailbox unavailable | The account authenticated but cannot open that mailbox. Check the account, and whether your environment expects an impersonated mailbox (deployment-managed `EXCHANGE_MAILBOX`) |
| Nothing happens when I save Settings | Configuration is per Windows account - make sure you are signed in as the account that saved it. A corrupt `%APPDATA%\EmailAI\settings.json` is quarantined once as `settings.json.corrupt` and the app continues with the deployment configuration; delete the file to start from scratch |

## FAQ

| Question | Answer |
| --- | --- |
| Do I need .NET, Node.js, Visual Studio or the source code? | No. The installer bundles the self-contained backend and the desktop shell. Those tools are needed only to modify or rebuild EmailAI ([Developer setup](#developer-setup)) |
| Do I need a GitHub account or a clone to install it? | No. Download the installer from the Releases page and run it |
| Does EmailAI work without an AI provider? | Yes. Folders, messages, threads and replies work normally; only the AI actions are disabled and the banner explains exactly what is missing |
| Does EmailAI work without Exchange? | It starts and reports an actionable "not configured" state, but there is no mailbox to read without an Exchange account |
| Where does my mail go? | Only the message or thread text of an AI action **you** trigger is sent to the OpenAI-compatible provider **you** configured. There is no EmailAI cloud service, no telemetry and no analytics |
| Can I use a non-OpenAI provider? | Yes - any OpenAI-compatible Chat Completions API, including a company gateway or a local model server |
| Which Windows versions are supported? | Windows 10 and Windows 11, **x64 only** - there is no x86 or ARM64 installer |
| Why does Windows warn when I install or start it? | The build is not code-signed, so SmartScreen cannot verify the publisher: choose *More info → Run anyway*. The `.sha256` file published next to the installer lets you verify the download |
| Can I use it with more than one mailbox? | Configuration and credentials are per **Windows user**, so sign in with a different Windows account for a second mailbox |
| Where is my API key / Exchange password? | In the Windows Credential Manager (targets `EmailAI/AI` and `EmailAI/Exchange`), scoped to your Windows account - never in the settings JSON, a log or the installer ([Storage and security](#storage-and-security)) |
| Does it ever send a reply by itself? | No. AI produces text only; a reply leaves the application when **you** press **Send reply** |
| Does it mark mail as read, or move/delete/compose? | Not in this version: it is a read + reply slice ([Current limitations](#current-limitations)) |
| Can I uninstall cleanly? | Yes - *Settings → Apps → EmailAI → Uninstall*. Then delete `%APPDATA%\EmailAI` and the two Credential Manager entries if you also want to remove your configuration |
| How do I know the download is intact? | Compare it with the published `EmailAI-Setup-<version>.exe.sha256`: `Get-FileHash .\EmailAI-Setup-1.1.0.exe -Algorithm SHA256` |

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

## Out of scope by design

- **Docker** (Dockerfile / compose) - not shipped. The backend is a single container-ready
  process if you want to host the browser version yourself.
- **Database / AI persistence, IMAP, Microsoft Graph, automatic email sending** - deliberately
  absent. EmailAI never sends anything the user did not send, and never stores mail content.

