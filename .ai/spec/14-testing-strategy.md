# 14 - Testing strategy

**Purpose.** Define what is verified automatically, what is verified by running the real
application, and what requires a real environment - so a "green build" has a precise meaning.

**Scope.** Test layers, their scope, the commands that run them, and the rules for opt-in real
tests. Feature-specific cases are listed in each specification's *Tests* section.

**Inputs.** The source tree, the test project, environment variables for the opt-in suites, the
packaged installer.

**Outputs.** A pass/fail signal per layer plus the release gates.

**Responsibilities.** `tests/EmailAI.Tests` (all automated layers), `desktop/scripts/release.js`
(release pipeline), `desktop/scripts/verify-secrets.js` (secret gate), the reviewer for the
manual/packaged checks.

**Layers.**

| Layer | What it proves | How it runs |
| --- | --- | --- |
| Unit | Pure logic: options/validation, endpoint building, sanitisation, direction, identity/aliases, notification detection, credential policy, status fan-out, folder keys, folder-sidebar state | `dotnet test tests/EmailAI.Tests -c Release` (offline) |
| Host (integration of the real HTTP surface) | The real ASP.NET Core host with in-memory stores: JSON contracts, status codes, headers, no-secret guarantees, prerendered HTML, the custom-folder children contract | same command (`WebApplicationFactory<Program>`, `SettingsHostFactory`) |
| Packaging contract | The files that define a release define a distributable one: artifact name derived from the version, per-user/x64 NSIS with the bundled backend, sample configuration with placeholders only, no development-only configuration, no private endpoint, CI that runs the same pipeline, documentation that names the real artifact | same command (`ReleasePackagingTests`; its scan-gate case runs `node` and reports a skip when Node.js is absent) |
| Pull-request CI | The gate a change must pass before it may reach `main`: the solution restores, builds and passes its tests, the Electron shell and the pipeline scripts parse, and the source tree carries no secret-shaped value, no development-only `appsettings.*.json` and no non-placeholder endpoint - the release gates without any packaging | `.github/workflows/ci.yml` on `windows-latest`; the branch ruleset requires its job, the check `Verify pull request`, before a merge |
| Opt-in real | A real AI chat completion and a real EWS probe, run only when the environment variables are present | `EMAILAI_AI_INTEGRATION_TEST=true` / `EMAILAI_EXCHANGE_INTEGRATION_TEST=true` + the `EMAILAI_*`/`EXCHANGE_*` variables |
| Shell | Electron JavaScript is syntactically valid and the real lifecycle works (start, health, UI load, quit, no orphan) | `node --check desktop/main.js`; run the packaged app with `EMAILAI_SMOKE_QUIT_MS` |
| Release | The installer exists, contains the fresh backend, carries no secret, no development configuration and no non-placeholder endpoint, and its SHA-256 is written next to it | `cd desktop; npm run release` |

**Invariants.**

1. The default test run is **fully offline** and deterministic: no real provider, no real
   mailbox, no network, no Windows Credential Manager (the stores are replaced with in-memory
   doubles).
2. Tests never read or write the developer's `%APPDATA%\EmailAI\settings.json` or their
   Credential Manager entries.
3. Opt-in real suites **skip cleanly**: when the environment is absent they report
   "not configured" and complete without calling any service - they never use fake credentials
   and never fake a success.
4. No test asserts on a real credential, hostname or account; fixtures use placeholders
   (`contoso.com`, `provider.test`, `ews.example.org`).
5. Every behavioural change ships with a test, including the failure path it introduces.
6. A release is only "built" if the pipeline's gates passed: tests, build, publish, packaging,
   scan, installer and checksum verification.
7. The packaging contract is pinned by tests: a renamed artifact, a shipped development
   configuration, a sample configuration with a real endpoint, a workflow that diverges from the
   local pipeline, an ignored documentation file or a README that names a non-existent installer
   all fail the suite.
8. The branch-protection contract is pinned by tests as well (`RepositoryWorkflowTests`):
   `.github/workflows/ci.yml` must keep its `pull_request` and `push` triggers pinned to `main`,
   run the documented gates in the documented order, never package anything, and keep its job name
   `Verify pull request` - the exact status check the `main - pull request workflow` ruleset
   requires, so the name must also appear in `README.md`, `CONTRIBUTING.md`, the pull-request
   template, `.ai/service-context.md` and specification 15. The release workflow must stay
   tag/dispatch-only. Renaming the check without updating those documents fails the suite instead of
   silently freezing `main` again.

**Failure modes.**

| Failure | Meaning |
| --- | --- |
| `dotnet test` red | A contract changed; the release pipeline stops before packaging |
| Opt-in suite reports "not configured" | Expected when the environment is absent - not a skipped failure |
| Electron `node --check` fails | The shell cannot run; fix before packaging |
| `verify-secrets` fails | A secret-shaped value reached the publish/package output; nothing is shipped |
| `verify-secrets` reports a development configuration or a non-placeholder endpoint | The publish/package is not distributable; nothing is shipped |
| `ReleasePackagingTests` red | A release-defining file changed without updating the contract (artifact name, samples, workflow, documentation, no-machine-path rule) |
| `RepositoryWorkflowTests` red | The change flow drifted from the documented policy: a CI trigger, a gate, the check name, the pull-request template or the contribution documentation no longer matches the protected-branch rules |
| `.github/workflows/ci.yml` red on a pull request | The ruleset keeps the pull request blocked (the required check `Verify pull request` is failing or missing); nothing reaches `main` |
| Installer missing after `npm run release` | The pipeline fails with the expected path and the artifacts it did find |

**Security constraints.** Test doubles are in-memory and secret-free; test output must not print
credentials (the integration suites never echo the key). Secret scanning is a mandatory release
gate (13).

**Configuration ownership.** Only the opt-in integration suites read environment configuration;
they are documented in `.env.example` and in the README's *Testing* section.

**Implementation.** `tests/EmailAI.Tests/**` (`SettingsHostFactory`, `TestDoubles`,
`IntegrationTestEnvironment`, `ReleasePackagingTests`, `RepositoryWorkflowTests`),
`desktop/scripts/release.js`, `desktop/scripts/verify-secrets.js`, `.github/workflows/release.yml`,
`.github/workflows/ci.yml`, `CONTRIBUTING.md`, `.github/PULL_REQUEST_TEMPLATE.md`.

**Tests.** The suite itself. Current state: **420 automated tests, 0 failures** (`dotnet test
tests/EmailAI.Tests -c Release`). Feature suites are named per specification; the folder-hierarchy
feature is pinned by `CustomFolderKeyTests`, `MailFolderEndpointsTests`, `MailFolderSidebarTests` and
`CustomMailFolderUiTests` (16).
