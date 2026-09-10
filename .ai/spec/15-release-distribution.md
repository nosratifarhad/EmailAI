# 15 - Release and distribution

**Purpose.** Produce one trustworthy Windows artifact from this source tree, and make it
downloadable by someone who will never build the project.

**Scope.** Versioning, the packaging pipeline, the artifact name/location, checksums, the
GitHub Release shape and CI automation. Feature behaviour is specified in 01-14.

**Inputs.** `desktop/package.json` (`version`, `productName`, electron-builder configuration),
the Release build of the solution, the self-contained win-x64 publish, the electron-builder
cache (Electron runtime + NSIS tooling).

**Outputs.** `desktop/dist/EmailAI-Setup-<version>.exe` (+ `.blockmap` and the `.sha256` checksum),
release notes, and - once published - a GitHub Release asset (installer + checksum).

**Responsibilities.** `desktop/scripts/release.js` (the pipeline), `desktop/package.json`
(packaging configuration), `desktop/scripts/verify-secrets.js` (gate),
`.github/workflows/release.yml` (CI), the releaser (tag + asset upload).

**Invariants.**

1. `desktop/package.json` `"version"` is the single source of truth; the installer name is derived
   from it through `build.nsis.artifactName` (`EmailAI-Setup-${version}.exe`), and `release.js` reads
   that same template instead of duplicating a name or a version.
2. The pipeline is **fail-fast**: no installer is produced unless tests, build, publish,
   packaging, scan and installer verification all pass.
3. The packaged backend is the **fresh** publish: the pipeline fails if the staged
   `resources/server/EmailAI.Api.exe` predates it.
4. A Release publish and the packaged backend contain **no development-only configuration**
   (`appsettings.Development.json` / `appsettings.Local.json`): `EmailAI.Api.csproj` excludes the
   development file for Release and `release.js` re-verifies its absence in both places.
5. Every endpoint inside a shipped `appsettings*.json` is a **documentation placeholder**; the scan
   fails the release on a real (internal or public) Exchange/AI endpoint.
6. The artifact is **x64 only** (`arch: [x64]`); there is no x86/ARM64 build, and this is
   documented rather than implied.
7. Build output (`desktop/dist/`, `desktop/aspnet-publish/`) is git-ignored: binaries are
   distributed as **release assets**, never committed to the source tree.
8. The installer contains only application payload: the Electron shell (`main.js` in
   `app.asar`), the self-contained backend, an uninstaller and Electron's own files. No source
   file, no development configuration, no `.env`, no secret.
9. The release is per-user (`perMachine: false`), assisted (not one-click) and lets the user
   choose the install folder.
10. The pipeline writes `<installer>.sha256` next to the artifact and prints the hash, so a download
    can be verified without trusting the transfer.
11. Release notes state what was validated and never claim a test or packaging step that did not
    run.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Toolchain missing (dotnet/npm/electron-builder) | Step 1 fails before anything is built |
| Test/build failure | Pipeline stops; the previous installer is not treated as current |
| Publish failure | Pipeline stops; no packaging against a stale publish |
| electron-builder failure | Pipeline stops with the builder output; no partial artifact is described as a release |
| Secret scan failure | Pipeline refuses to ship the build |
| Development-only configuration or a non-placeholder endpoint in the publish/package | The scan fails the release (never silently included) |
| No code-signing certificate | The installer is unsigned; SmartScreen may warn (documented). Signing can be added later without changing the pipeline shape (`CSC_*` variables) |
| Asset upload not possible (no remote/credentials in the environment) | The installer and its checksum still exist locally with a known path and SHA-256; publishing is an explicit, documented manual step |

**Security constraints.** The pipeline never prints secrets; the secret scan covers both the
publish and the staged packaged backend. Distribution must not include real Exchange hostnames,
accounts or credentials - only placeholders.

**Configuration ownership.** Version and packaging configuration: `desktop/package.json`.
Release publishing: the repository owner (tag + asset). Runtime configuration: 02/05/13.

**Implementation.** `desktop/package.json`, `desktop/scripts/release.js`,
`desktop/scripts/verify-secrets.js`, `desktop/main.js`, `.github/workflows/release.yml`,
`RELEASE-NOTES.md`, `.gitignore`.

**Tests.** `cd desktop; npm run release` (tests → build → publish → package → scan → installer +
checksum verification). The packaging contract itself is pinned by
`tests/EmailAI.Tests/ReleasePackagingTests.cs` (artifact name, NSIS shape, samples, workflow,
documentation, no private endpoint, and the scan gate executed against planted defects). Current
result is recorded in `RELEASE-NOTES.md`.
