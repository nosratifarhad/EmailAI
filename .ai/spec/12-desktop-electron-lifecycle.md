# 12 - Desktop (Electron) lifecycle

**Purpose.** Define exactly what the Windows shell does, so the desktop experience (startup,
shutdown, notifications, deep links, packaging) stays predictable and never leaves orphan
processes.

**Scope.** `desktop/main.js` (process lifecycle, window, notifications, deep links),
`desktop/package.json` (packaging configuration) and the `desktop/scripts/*` helpers. The
backend's own behaviour is specified in 01-11.

**Inputs.** The packaged app (Electron + `resources/server/EmailAI.Api.exe`), the user's
environment (which the shell forwards to the backend), OS notification support.

**Outputs.** A running window showing the Blazor UI at `http://127.0.0.1:<free port>`; native
toasts for new mail; log files (`main.log`, `server.out.log`, `server.err.log`) and a
`server-state.json` (status/pid/port) under `%APPDATA%\EmailAI`.

**Responsibilities.** `desktop/main.js` only. Electron is a shell: it must never implement mail
or AI logic.

**Invariants.**

1. The backend is started with a **free loopback port** and `ASPNETCORE_URLS` set to it, so a
   port conflict cannot break startup.
2. The UI is loaded only after `/health` answers 200 (poll every 250 ms, up to 60 s, with up to 4
   start attempts), and a splash window covers the wait.
3. Single instance: a second launch focuses the existing window (`requestSingleInstanceLock`).
4. On quit (`will-quit`) the shell stops the notification timer **and** the backend process -
   no orphan `EmailAI.Api.exe` remains.
5. Navigation outside the app origin is blocked and opened in the system browser; the splash
   (`data:text/html`) is the only other allowed URL.
6. The renderer never receives secrets: no key, no password, no provider base URL is injected
   into the page.
7. Notifications obey 09 (baseline, de-duplication server-side, header-only payload, click →
   `/?item=<id>`).
8. The shell forwards its environment, so `EMAILAI_DISABLE_NOTIFICATIONS`,
   `EMAILAI_SMOKE_QUIT_MS` and `ASPNETCORE_ENVIRONMENT` work as documented.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Bundled backend missing | Error dialog "Bundled backend not found at …", exit code 1 |
| Backend never becomes healthy | Up to 4 attempts, then an error dialog and exit - no half-started window |
| Backend exits unexpectedly while running | Logged; state file updated to `exited`; the app does not silently pretend to work |
| UI load failure | Logged, error dialog with the message |
| Notifications unsupported / disabled | Logged; the app runs normally without toasts |
| OS notification shown | Toast is passive: clicking restores/focuses and deep-links |

**Security constraints.** The window loads only loopback URLs; external links never load inside
the app. Logs contain process/port/health facts and error messages - never mail content and never
credentials. The state file holds no secret.

**Configuration ownership.** Packaging configuration (`productName`, `appId`, NSIS options,
`extraResources`) lives in `desktop/package.json`; runtime toggles are environment variables for
the desktop process (`EMAILAI_*`). Mail/AI configuration stays in the backend (02, 05).

**Implementation.** `desktop/main.js`, `desktop/package.json`, `desktop/build/icon.png`,
`desktop/scripts/release.js`, `desktop/scripts/verify-secrets.js`.

**Tests.** JavaScript syntax checks (`node --check desktop/main.js`,
`node --check desktop/scripts/*.js`); the notification and endpoint contracts are covered by
`MailNotificationServiceTests` / `NotificationEndpointsTests`; the real lifecycle is exercised by
running the packaged app with `EMAILAI_SMOKE_QUIT_MS` and asserting that the backend starts,
`/health` answers, the UI loads and **no orphan process remains** (see 15).
