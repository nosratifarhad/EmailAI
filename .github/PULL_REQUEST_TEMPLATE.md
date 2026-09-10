## What changed

<!-- One paragraph: the behaviour that changed and why. Link the issue it fixes, if any. -->

## How it was verified

<!-- The exact commands you ran and their result. CI runs the same commands as the required
     check "Verify pull request" (.github/workflows/ci.yml, windows-latest). -->

- [ ] `dotnet build EmailAI.slnx -c Release` - 0 warnings / 0 errors
- [ ] `dotnet test EmailAI.slnx -c Release` - all tests green
- [ ] `node --check desktop/main.js`, and the release scripts
      (`desktop/scripts/release.js`, `release-version.js`, `verify-release.js`, `release-notes.js`,
      `verify-secrets.js`)
- [ ] `cd desktop; node scripts/verify-secrets.js --allow-development-config ../src ../.env.example`
- [ ] packaging/release change only: `cd desktop; npm run release` (the version gate, packaging and
      artifact verification), and `node scripts/verify-release.js --tag v<version>` when the rule
      under test is the release identity

## Checklist

- [ ] The change is one logical change and can be squash-merged into `main`
- [ ] Tests cover the new/changed behaviour (or this PR states why they cannot)
- [ ] Documentation that describes the behaviour is updated (`README.md`, `.ai/service-context.md`, `.ai/spec/*`)
- [ ] No secret, credential, real Exchange/AI endpoint, development-only `appsettings.*.json`,
      machine-specific path or personal account is introduced anywhere
- [ ] The pull request targets `main` (direct pushes to `main` are blocked by the repository ruleset)
