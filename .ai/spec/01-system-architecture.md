# 01 - System architecture

**Purpose.** Define how EmailAI is put together so every other specification can name its
layer, its owner and its trust boundary.

**Scope.** Process and layer topology, traffic paths, and which communication leaves the
machine. Not in scope: individual feature contracts (see 02-15).

**Inputs.** User actions in the UI, deployment environment variables, the per-user settings
document, the packaged backend binary.

**Outputs.** A running local application: an Electron window that shows the Blazor UI served by
the bundled ASP.NET Core backend, which talks to Exchange (EWS) and to the configured AI
provider.

**Responsibilities.**

| Layer | Owns |
| --- | --- |
| `desktop/` (Electron) | Choosing a free loopback port, starting/stopping the bundled backend, the window, native notifications, deep-link loading |
| `src/EmailAI.Api` | REST endpoints, the Blazor Web App UI, typed UI API client (`EmailApiClient`), per-user settings endpoints, status notifier |
| `src/EmailAI.Application` | Business rules: AI prompts/services, HTML sanitiser, notification detection, settings/credential contracts, Exchange identity contracts |
| `src/EmailAI.Domain` | DTOs and option types shared by all layers (`Mail`, `Exchange`, `AI`, `Settings`) |
| `src/EmailAI.Infrastructure` | EWS implementation (Exchange), OpenAI-compatible HTTP client, file + Credential Manager stores |

**Invariants.**

1. The UI and the API are **one origin**; the browser/Electron never calls EWS or the AI
   provider directly.
2. The backend listens only on `127.0.0.1` with a port chosen by the shell.
3. AI requests are made **server-side only**; the renderer holds no key, no base URL and no
   model credentials.
4. Only two data paths leave the machine: EWS/SOAP to the configured Exchange endpoint, and
   HTTPS to the configured AI provider. There is no EmailAI service in the middle.
5. Exactly one process serves the UI; there is no second web server and no CORS configuration.
6. The mailbox folder hierarchy the UI shows is read from Exchange through the same authenticated
   EWS connection, and it adds no data path of its own (16).

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Bundled backend missing/not starting | The shell shows an error dialog and exits non-zero; it never leaves an orphan process (`will-quit` stops the child) |
| Backend healthy but Exchange unreachable | The app still starts; mail endpoints answer typed errors and the header shows the state |
| AI provider unreachable | The app still starts; AI actions are disabled with an explanation |
| Circuit/session lost | Blazor reconnects (`ReconnectModal`); mail state is re-read on demand |

**Security constraints.** The renderer is treated as untrusted for secrets; navigation away from
the app origin is blocked and opened externally. Email HTML is untrusted (see 08).

**Configuration ownership.** Deployment/administrator: environment variables and
`appsettings.json` (placeholders only). Desktop user: the Settings page (see 02, 13).

**Implementation.** `desktop/main.js`, `src/EmailAI.Api/Program.cs`,
`src/EmailAI.Api/Components/**`, `src/EmailAI.Api/Web/EmailApiClient.cs`,
`src/EmailAI.Application/**`, `src/EmailAI.Infrastructure/**`.

**Tests.** `AiSettingsEndpointsTests.SettingsHostFactory` (real host bootstrap),
`InitialInboxLoadTests`, `AiLanguageEndpointTests`, `NotificationEndpointsTests`.
