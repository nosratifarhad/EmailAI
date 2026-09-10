# EmailAI 1.1.0 - Release Notes

**Windows x64 · `EmailAI-Setup-1.1.0.exe` · `EmailAI-Setup-1.1.0.exe.sha256`**

This is the **first end-user distribution release**: a normal user can download the installer,
run it, configure Exchange and their AI provider, and use the application without ever building
the source tree.

---

## Highlights

* **AI-assisted mail workflow** - summarize a message, summarize a whole conversation, suggest a
  reply, or generate an editable reply draft. Nothing is ever sent automatically: the only
  sender is the reply button.
* **Current-user identity** - every AI operation is given the mailbox owner resolved from the
  Exchange account and directory (display name, SMTP address, account), never inferred from the
  email text. In this release the directory lookup also tries the bare account name, which a real
  Exchange directory answers for (the `DOMAIN\user` form returns nothing), so the identity is
  authoritative instead of degrading to the local Windows account.
* **HTML rendering with a hard security boundary** - message HTML is sanitized (scripts, frames,
  forms, event handlers and dangerous URLs removed, obfuscation decoded) and rendered in a
  sandboxed, script-less iframe with a restrictive CSP. Plain-text fallback and an explicit
  "no body" state are always available.
* **Native Windows notifications** - one toast per newly arrived message (sender + subject only),
  with a silent baseline on start, server-side de-duplication, and a click that focuses the
  window and opens exactly that message.
* **AI readiness UX** - the app states up front whether the assistant can be used and why not
  (not configured, no key yet, endpoint unreachable, key rejected, rate limited, timeout), with a
  one-click path to Settings and no raw HTTP status or exception text.
* **Persian/English response direction** - choose the response language: Persian renders RTL and
  right-aligned, English LTR and left-aligned. The explicit choice wins over browser language.
* **Immediate settings synchronization** - saving (or removing) AI/Exchange configuration updates
  the header status and the mail view at once, without a reload or restart.
* **Reliable initial Inbox** - the first paint already contains the Inbox, and one **bounded
  retry** (no artificial delay) absorbs the cold-start hiccup of the very first Exchange call.

## Fixed in this release

* **AI operation language contract.** The documented request body `{"language":"English"}` /
  `{"language":"Persian"}` was rejected with an **empty HTTP 400** (only the numeric enum value
  worked, which the shipped UI happened to send). Both forms now work, and an unsupported value
  answers `400 {"error": "…not supported. Use Auto, English or Persian."}` instead of a
  body-less 400.
* **Directory identity resolution** (see Highlights) - candidate names are now tried in order
  within one authenticated EWS session.
* **Initial Inbox resilience** - a transient connection failure on the first load is retried once
  before the user is shown an error; a genuine failure still surfaces unchanged.

## Security

* Secrets (Exchange password, AI API key) live **only** in the Windows Credential Manager under
  their own targets (`EmailAI/Exchange`, `EmailAI/AI`) - never in the settings JSON, the
  installer, a log or an API response. The release pipeline secret-scans the published and
  packaged output and fails the build if a secret-shaped assignment is found.
* Email HTML is treated as **untrusted** and can never execute script, submit a form or navigate
  the application.
* Notification payloads contain **no message body** and no credential.
* The AI provider receives **no credential** other than its own API key (sent as
  `Authorization: Bearer`), and the prompt never contains a password or token.
* Exchange authentication is explicit and single-mode: there is **no implicit NTLM or
  username/password fallback**.

## Validation performed for this release

| Check | Result |
| --- | --- |
| `dotnet build EmailAI.slnx -c Release` | 0 warnings, 0 errors |
| `dotnet test tests/EmailAI.Tests -c Release` | 366 passed, 0 failed |
| `node --check desktop/main.js` (and `desktop/scripts/*.js`) | pass |
| Source-tree scan (`verify-secrets.js --allow-development-config src .env.example`) | pass - no secret value, no non-placeholder endpoint |
| `npm run release` (tests → build → publish → NSIS package → secret scan → verify) | pass, 125.7 s, installer + SHA-256 file produced |
| Scan of the Release publish and the packaged backend (359 files each) | pass - no secret, **no development-only configuration**, no non-placeholder endpoint |
| Release publish / packaged backend contains `appsettings.Development.json` | no (excluded for Release; verified in both places) |
| Packaging contract suite (`ReleasePackagingTests`) | pass (13 tests): artifact name derived from the version, per-user/x64 NSIS, bundled backend, placeholder-only samples, workflow on the same pipeline, docs naming the real artifact, no machine-specific path - and the scan gate failing on a planted secret/development configuration while never echoing the secret |
| Secret scan of the publish + staged packaged backend | pass |
| Installer / installed payload | `EmailAI-Setup-1.1.0.exe`; after a silent install the tree is the shell + `resources/server` (the sample `appsettings.json` **only** - no development configuration) + the uninstaller |
| Authenticode | `NotSigned` (no code-signing certificate available) |
| Silent install on Windows x64 | exit code 0, in-place upgrade, uninstaller present |
| Packaged-app lifecycle, launched from the install folder (`EMAILAI_SMOKE_QUIT_MS=25000`) | version 1.1.0 started, backend healthy in **899 ms** on a free loopback port, UI load requested, notifications enabled (inbox, 30 s), backend stopped on quit, **0 orphan processes** |
| Real Exchange through the installed backend (`GET /health/exchange`) | `healthy`, 802 ms |
| Real AI provider through the installed backend (`GET /health/ai`) | `connected`, 270 ms |
| AI readiness through the installed backend (`GET /api/ai/readiness`) | `Ready`, `ai_ready` |
| Real inbox through the installed backend (`GET /api/folders/inbox/messages`) | 5 items returned (`hasMore`), real mailbox |
| Downloads verified against the published checksum | recomputed SHA-256 identical to the `.sha256` file |

## Artifact

| Field | Value |
| --- | --- |
| File | `EmailAI-Setup-1.1.0.exe` |
| Size | 150,815,654 bytes (143.8 MB) |
| SHA-256 | `85eb6666fb3fa1a39a71ac0d15487797946e537b899b697746d6ce551cd83efa` |
| Checksum file | `EmailAI-Setup-1.1.0.exe.sha256` |
| Signature | Not code-signed (`NotSigned`) |
| Platform | Windows 10 / 11, x64, per-user NSIS installer (no administrator rights) |

## Install

1. Download `EmailAI-Setup-1.1.0.exe` (and `EmailAI-Setup-1.1.0.exe.sha256` if you want to verify it:
   `Get-FileHash .\EmailAI-Setup-1.1.0.exe -Algorithm SHA256`).
2. Run it (per-user install, no administrator rights). Windows SmartScreen may warn because the
   build is not code-signed: *More info → Run anyway*.
3. Launch **EmailAI**, open **Settings** and configure **Exchange** and your **AI provider**
   (both cards can test the connection).
4. Read your Inbox and use the AI actions.

Windows 10/11 **x64** only. The backend is bundled self-contained - no .NET runtime and no
Node.js are required.

## Known limitations

* **Not code-signed** - SmartScreen may warn on first launch; an internal distribution channel
  could add signing without changing the build pipeline.
* **x64 only** - no x86 or ARM64 installer.
* Read + reply slice: no compose-new, move, delete or read/unread marking; attachment
  **download** is not implemented (metadata is listed only).
* Very long threads are bounded: each message is truncated to a representative slice.
* Draft quality depends entirely on the configured model.

## Upgrade / uninstall

Upgrades install in place and keep your configuration (it is stored per Windows user, outside the
install folder). Uninstall from *Settings → Apps → Installed apps → EmailAI*; saved settings and
Credential Manager entries are removed only if you delete them yourself.

## Distribution note

The installer for this version was built, scanned and verified in the release environment (see the
validation table). This working copy has **no Git remote configured** and no GitHub tooling
available, so the artifact has not been uploaded to a GitHub Release from here: publishing it as the
release assets `EmailAI-Setup-1.1.0.exe` and `EmailAI-Setup-1.1.0.exe.sha256` under the tag `v1.1.0`
is the one remaining manual step, and `.github/workflows/release.yml` performs it automatically on a
`v*.*.*` tag push.
