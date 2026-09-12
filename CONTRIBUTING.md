# Contributing to EmailAI

EmailAI is a Windows desktop mail client for Microsoft Exchange with a built-in AI assistant. It is
maintained by a **single maintainer** (`nosratifarhad`), so this process is deliberately small - but
`main` is protected: **every change, including the maintainer's, reaches `main` through a pull
request** and must pass the repository's automated check first.

Bug reports and focused pull requests are welcome. There is no mailing list, no CLA and no sign-off
requirement.

---

## 1. Fork, branch, change

1. **Fork** the repository on GitHub (if you have write access, clone it directly).
2. **Create a feature branch** off `main` - never commit to `main`; direct pushes are rejected by
   the server (see [section 4](#4-branch-protection-on-main)):

   ```powershell
   git clone https://github.com/<your-account>/EmailAI.git
   cd EmailAI
   git switch -c fix/short-description main
   ```

3. **Make the change.** One behaviour per pull request. Keep the documentation honest: the
   specifications in `.ai/spec/` and the statements in `README.md` describe the shipped behaviour,
   so a behaviour change usually touches a document as well.
4. **Add or update tests** for the behaviour you changed (see
   `.ai/spec/14-testing-strategy.md`). The default suite is fully offline and deterministic.
5. **Never commit** a real credential, a real Exchange/AI endpoint, a machine-specific path or a
   development-only `appsettings.*.json`. `desktop/scripts/verify-secrets.js` is a gate, not advice,
   and it runs on every pull request.

## 2. Run the same verification locally

The required check `Verify pull request` runs exactly these commands on `windows-latest`
(`.github/workflows/ci.yml`):

```powershell
dotnet restore EmailAI.slnx
dotnet build   EmailAI.slnx -c Release         # 0 warnings / 0 errors
dotnet test    EmailAI.slnx -c Release         # offline unit + host + contract tests

node --check desktop/main.js
node --check desktop/scripts/release.js
node --check desktop/scripts/release-version.js
node --check desktop/scripts/verify-release.js
node --check desktop/scripts/release-notes.js
node --check desktop/scripts/verify-secrets.js

cd desktop
npm ci
node scripts/verify-secrets.js --allow-development-config ../src ../.env.example
node scripts/verify-release.js --tag "v$(node -p "require('./package.json').version")"   # tag vs package.json
```

Only a packaging change additionally needs the release pipeline: `cd desktop; npm run release`.

## 3. Open the pull request

Open the pull request **against `main`** and fill in
[`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md) (GitHub applies it
automatically): what changed, how you verified it, and the checklist. The workflow described in
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) then starts on its own - it appears under
Actions as the job `Verify pull request`, which is the check the ruleset requires.

A pull request **cannot be merged** while `Verify pull request` is failing, missing or stale, while
its branch is behind `main`, or while a review conversation is unresolved.

## 4. Branch protection on `main`

`main` is protected by two repository rulesets that apply to `refs/heads/main` and to nothing else,
with **no bypass actors at all** - the rules apply to the maintainer exactly as they apply to a
first-time contributor:

| Ruleset | Rule | Effect |
| --- | --- | --- |
| `main - integrity` | Restrict deletions | `main` cannot be deleted |
| `main - integrity` | Block force pushes | a non-fast-forward update of `main` is rejected |
| `main - pull request workflow` | Require a pull request before merging | a direct push to `main` is rejected - changes arrive only through a pull request |
| `main - pull request workflow` | Required approvals: **0** | no review is machine-required (single maintainer - GitHub refuses a self-approval); the maintainer still reviews every pull request by hand before merging |
| `main - pull request workflow` | Merge method: **squash only** | merge commits and rebase merges into `main` are rejected |
| `main - pull request workflow` | Require status checks: `Verify pull request` (strict) | the branch must be up to date with `main` and the CI job must have passed |

The matching repository settings are squash-only as well: merge commits, rebase merges and auto-merge
are disabled, squash merging is enabled, and the head branch is deleted automatically after a merge.

**Why "0 approvals" and not 1?** With a single maintainer account, GitHub rejects a self-approval
("Can not approve your own pull request"), so a required approval would freeze `main` permanently.
The pull request - with its CI check, its discussion and its squash commit - stays the mandatory
record of every change. **As soon as a second maintainer (account or `maintainers` team) exists**,
raise `required_approving_review_count` to `1` and re-enable "dismiss stale reviews" and "require
approval of the latest push"; nothing else has to change.

## 5. Review and merge

* The maintainer reviews the diff, the discussion and the green `Verify pull request` check.
* Merging is **squash merge** only; the pull request title and body become the commit message.
* The head branch is deleted automatically after the merge.
* Security-sensitive reports: open a minimal issue (or contact the maintainer directly) instead of
  posting credentials - never paste a real password, key or mailbox into an issue or a test.

## 6. Releases

Releases are **not** produced by pull requests. `.github/workflows/release.yml` builds the Windows
x64 installer only for a `v*.*.*` tag (or a manual dispatch that names the tag): `npm run release`
runs the tests, build, publish, NSIS packaging, the secret/configuration scan and the release
verification (installer name, checksum, the version embedded in the installer, generated notes).

A version bump belongs in `desktop/package.json` (`npm version <x.y.z> --no-git-tag-version`) and
lands through a pull request like any other change; the maintainer pushes the tag afterwards, and the
release workflow attaches `EmailAI-Setup-<version>.exe` and its `.sha256` to the GitHub Release.

**One version identity.** The tag must equal `desktop/package.json` `"version"`, and the workflow
checks that *before* it builds anything:

```powershell
cd desktop
node scripts/verify-release.js --tag v1.3.2   # PASS, or the release stops with expected/actual/sources
```

A tag that disagrees with the package is a release configuration error: the pipeline never edits the
version to fit a tag, never renames an artifact and never reuses a previous installer. If you tag the
wrong commit or the wrong version, delete the tag (or push a corrected one) and re-run - never
rewrite a published release.

## 7. Licensing

**This repository currently declares no license** (`desktop/package.json` is marked `UNLICENSED`
and there is no `LICENSE` file), and the owner has not chosen one yet. Until that changes, the source
may not be reused in another product. Issues and pull requests are welcome regardless; by opening a
pull request you agree that your contribution may be distributed under the license the owner
eventually selects.


