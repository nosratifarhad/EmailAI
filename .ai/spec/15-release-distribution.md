# 15 - Release and distribution

**Purpose.** Produce one trustworthy Windows artifact from this source tree, and make it
downloadable by someone who will never build the project.

**Scope.** Versioning, the packaging pipeline, the artifact name/location, checksums, the
GitHub Release shape and CI automation. Feature behaviour is specified in 01-14.

**Inputs.** `desktop/package.json` (`version`, `productName`, electron-builder configuration), the
release **Git tag** (`vX.Y.Z`), the Release build of the solution, the self-contained win-x64 publish,
the electron-builder cache (Electron runtime + NSIS tooling).

**Outputs.** `desktop/dist/EmailAI-Setup-<version>.exe` (+ `.blockmap` and the `.sha256` checksum),
`desktop/dist/RELEASE-NOTES.md` (the generated release body), and - once published - a GitHub Release
titled `EmailAI <version>` carrying the installer and its checksum.

**Responsibilities.** `desktop/scripts/release-version.js` (the version identity - resolvers and
every check), `desktop/scripts/verify-release.js` (the standalone gate), `desktop/scripts/release.js`
(the pipeline), `desktop/scripts/release-notes.js` (the release body), `desktop/package.json`
(packaging configuration), `desktop/scripts/verify-secrets.js` (secret/config gate),
`.github/workflows/ci.yml` (the pull-request gate), `.github/workflows/release.yml` (release CI), the
releaser (tag + asset upload).


**Invariants.**

1. `desktop/package.json` `"version"` is the single source of truth for the application version. It
   is read only through `desktop/scripts/release-version.js`; no script, workflow, package script or
   document in the release path duplicates a version, and no component may fall back to a previous
   release's version (a hard-coded version literal in the release machinery is a defect, and
   `ReleaseVersionTests` fails on one).
2. One version identity: `tag == desktop/package.json version == desktop/package-lock.json version
   == installer name == installer metadata == checksum file name == release title/notes`. A release
   is identified by the Git tag `vX.Y.Z` and nothing else.
3. The pipeline is **fail-closed** on that identity: the release workflow validates the tag against
   the package *before* it installs, restores or builds anything, and the tag must point at the
   commit being built. A disagreement stops the release with the expected version, the actual
   version, the source of each and how to fix it - it is never resolved by editing the version,
   renaming an artifact or reusing a previous installer.
4. The installer is generated with the release version in its name (`EmailAI-Setup-<version>.exe`)
   and in its version metadata (electron-builder writes `package.json`'s version into the NSIS
   installer, verified as `FileVersion`/`ProductVersion`). A differently named installer in the
   packaging output - including a previous release's installer - fails the release.
5. `.sha256` is calculated from the exact installer file that is published and is verified by
   re-reading both files; it is never calculated from another build, another directory, a cached
   artifact or a differently named copy.
6. The packaging output is clean: `desktop/dist/` and `desktop/aspnet-publish/` are removed before
   packaging, and the release fails unless exactly one installer with the expected name exists.
7. The release notes are generated for the version being released
   (`desktop/scripts/release-notes.js`): the heading, the installer, the checksum and the change list
   come from that version, that installer file and the real Git history since the previous release
   tag. Notes describing another version fail the release.
8. `desktop/package.json` `"version"` names the installer through `build.nsis.artifactName`
   (`EmailAI-Setup-${version}.exe`), and `release-version.js` reads that template instead of
   duplicating a name.
9. The pipeline is **fail-fast**: no installer is produced unless tests, build, publish, packaging,
   scan and verification all pass.
10. The packaged backend is the **fresh** publish: the pipeline fails if the staged
    `resources/server/EmailAI.Api.exe` is missing or predates it.
11. A Release publish and the packaged backend contain **no development-only configuration**
    (`appsettings.Development.json` / `appsettings.Local.json`): `EmailAI.Api.csproj` excludes the
    development file for Release and `release.js` re-verifies its absence in both places.
12. Every endpoint inside a shipped `appsettings*.json` is a **documentation placeholder**; the scan
    fails the release on a real (internal or public) Exchange/AI endpoint.
13. The artifact is **x64 only** (`arch: [x64]`); there is no x86/ARM64 build, and this is
    documented rather than implied.
14. Build output (`desktop/dist/`, `desktop/aspnet-publish/`) is git-ignored: binaries are
    distributed as **release assets**, never committed to the source tree.
15. The installer contains only application payload: the Electron shell (`main.js` in `app.asar`),
    the self-contained backend, an uninstaller and Electron's own files. No source file, no
    development configuration, no `.env`, no secret.
16. The release is per-user (`perMachine: false`), assisted (not one-click) and lets the user choose
    the install folder.
17. The published release body is the generated `desktop/dist/RELEASE-NOTES.md`, and the GitHub
    Release is titled `EmailAI <version>`, created for the validated tag (a manual dispatch produces
    a draft). The checked-in `RELEASE-NOTES.md` is the record of the current version and names the
    current installer.
18. Publishing uses the exact verified file names - never a `EmailAI-Setup-*.exe` wildcard, which
    could pick up a stale installer from an earlier release.
19. Release notes state what was validated and never claim a test or packaging step that did not
    run; the change list is derived from real commits and no changelog entry is invented.
20. Packaging has exactly one trigger: a `v*.*.*` tag or a manual dispatch (which must name the tag)
    of `.github/workflows/release.yml`. `.github/workflows/ci.yml` (pull requests and pushes to
    `main`) runs build, tests, the script syntax checks and the scan gates but **never** publishes,
    packages or uploads an artifact, so a pull request cannot consume the NSIS toolchain or release
    permissions. Guarding the shared release scripts is therefore CI work, not release work:
    `ReleaseVersionTests` runs them on every pull request.


**Change flow.** Every change - the maintainer's included - reaches `main` through a pull request.
`main` is protected by two repository rulesets that target `refs/heads/main` only and have **no
bypass actors**: `main - integrity` blocks force pushes and deletion, and
`main - pull request workflow` requires a pull request with the strict status check
`Verify pull request` (produced by `.github/workflows/ci.yml`) and allows squash merges only.
Required approvals are **0**, because a single maintainer cannot approve their own pull request and
a required approval would freeze the branch; the maintainer reviews every pull request by hand
before merging. Raise `required_approving_review_count` to `1` once a second maintainer exists.
`CONTRIBUTING.md` is the contributor-facing statement of this policy.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Toolchain missing (dotnet/npm/electron-builder) | Step 1 fails before anything is built |
| Git tag disagrees with `desktop/package.json` (or its lock file) | The release stops before anything is installed or built, printing the expected version (from the tag), the actual version (from the package), the source of each and how to fix it |
| The tag does not point at the commit being built | Same gate; the release is never built from an untagged commit and then published under a tag |
| A version literal in the release machinery | `ReleaseVersionTests` fails on the pull request - a stale literal is the fallback that shipped a mismatching release |
| No installer / more than one installer / an installer named for another version | Step 7 and the workflow gate fail; the stale file is never renamed, uploaded or published |
| A `.sha256` that does not match the installer, or a missing `.sha256` | Step 7 and the workflow gate fail; the installer is never published without the checksum of the exact file |
| The version embedded in the installer differs from the release version | Step 7 and the workflow gate fail (a correctly named file is not enough) |
| Release notes describing another version | Step 7 and the workflow gate fail; the notes are generated for the version being released |
| Test/build failure | Pipeline stops; the previous installer is not treated as current |
| Publish failure | Pipeline stops; no packaging against a stale publish |
| The staged backend is missing or predates the publish | Step 7 fails (the installer must carry this build) |
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

**Implementation.** `desktop/package.json`, `desktop/package-lock.json`,
`desktop/scripts/release-version.js` (the version identity and every check),
`desktop/scripts/verify-release.js` (the standalone gate), `desktop/scripts/release.js`,
`desktop/scripts/release-notes.js`, `desktop/scripts/verify-secrets.js`, `desktop/main.js`,
`.github/workflows/ci.yml`, `.github/workflows/release.yml`, `CONTRIBUTING.md`,
`.github/PULL_REQUEST_TEMPLATE.md`, `RELEASE-NOTES.md`, `.gitignore`.

**Tests.** `cd desktop; npm run release` (version identity -> tests -> build -> publish -> package ->
scan -> installer + checksum + installer metadata + notes verification). The identity, artifact,
checksum and notes rules are pinned by `tests/EmailAI.Tests/ReleaseVersionTests.cs`, which **runs**
the real gate and notes generator - a matching tag, a mismatching tag, a tag that is not `vX.Y.Z`, a
tag pointing at another commit, an installer named for another version, a checksum belonging to
another file, a missing checksum file, an installer without version metadata, notes describing
another version, and the absence of any hard-coded version in the release machinery. The packaging,
workflow and documentation contract is pinned by `tests/EmailAI.Tests/ReleasePackagingTests.cs`
(artifact name, NSIS shape, samples, workflow gate order and exact-artifact upload, documentation, no
private endpoint, and the scan gate executed against planted defects), and the change flow by
`tests/EmailAI.Tests/RepositoryWorkflowTests.cs` (CI triggers, ordered gates, no packaging in
`ci.yml`, the required check name `Verify pull request` in every document that states the policy, and
a tag/dispatch-only release workflow). Current result is recorded in `RELEASE-NOTES.md`.

