# EmailAI specification set

These files are the **authoritative, implementation-aligned specifications** for EmailAI. They
follow the repository's existing documentation layout (`.ai/`); the older summary document
[`../service-context.md`](../service-context.md) links here and remains the quick overview of the
two external integrations.

Every specification uses the same headings so a change can be reviewed the same way everywhere:

| Heading | Meaning |
| --- | --- |
| Purpose | Why the area exists |
| Scope / not in scope | What it covers and what it deliberately does not |
| Inputs | What it consumes (config, requests, upstream data) |
| Outputs | What it produces (responses, side effects, state) |
| Responsibilities | Which layer owns the behaviour |
| Invariants | Statements that must stay true; violating one is a defect |
| Failure modes | Every way it can fail and what happens then |
| Security constraints | What must never leak or be trusted |
| Configuration ownership | Who may configure what (deployment vs per-user) |
| Implementation | Files that implement it |
| Tests | Suites that pin the behaviour |

| # | Specification | Covers |
| --- | --- | --- |
| 01 | [System architecture](01-system-architecture.md) | Layers, processes, local vs off-machine traffic |
| 02 | [Exchange configuration](02-exchange-configuration.md) | Effective endpoint/mode/account, precedence |
| 03 | [Exchange authentication](03-exchange-authentication.md) | `Windows` / `UsernamePassword`, no fallback |
| 04 | [Current-user identity](04-current-user-identity.md) | Mailbox owner resolution and provenance |
| 05 | [AI architecture](05-ai-architecture.md) | OpenAI-compatible provider client, endpoints, limits |
| 06 | [AI readiness](06-ai-readiness.md) | Pre-flight state report and user messages |
| 07 | [AI prompt trust boundaries](07-ai-prompt-trust-boundaries.md) | Trusted identity vs untrusted email data |
| 08 | [Email rendering and security](08-email-rendering-security.md) | HTML sanitiser, sandbox, CSP |
| 09 | [Notifications](09-notifications.md) | New-mail feed, baseline, de-duplication, deep link |
| 10 | [Settings and state synchronization](10-settings-state-synchronization.md) | Immediate status updates after a save |
| 11 | [Initial Inbox loading](11-initial-inbox-loading.md) | First-paint content and bounded retry |
| 12 | [Desktop (Electron) lifecycle](12-desktop-electron-lifecycle.md) | Backend start/stop, window, packaging hooks |
| 13 | [Secret management](13-secret-management.md) | Credential Manager targets, no plaintext secrets |
| 14 | [Testing strategy](14-testing-strategy.md) | Unit, host, integration, packaging, secret scan |
| 15 | [Release and distribution](15-release-distribution.md) | Versioning, packaging, artifact, release assets |
| 16 | [Mail folders and custom folder navigation](16-mail-folders.md) | Folder hierarchy, custom folder discovery, folder selection, message retrieval |

Rules for keeping these documents honest:

* Every claim here is verifiable in the named files or tests - no aspirational behaviour.
* If code and specification disagree, the **specification wins or is corrected**; a silent
  divergence is a bug in this set.
* Never paste a real Exchange hostname, account or credential into these files. Placeholders
  (`mail.contoso.com`, `host.example`) only.
