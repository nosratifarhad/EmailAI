'use strict';

// =============================================================================
// EmailAI local Windows release - one command, run from the repository's desktop folder:
//
//     cd desktop
//     npm run release
//
// Pipeline (fail-fast - if any step fails, no installer is produced):
//   1. Validate the project layout, the toolchain and the release version identity: the Git tag
//      (--tag / EMAILAI_RELEASE_TAG, or the tag at HEAD), desktop/package.json and its lock file
//      must describe one version (scripts/release-version.js). A tag that disagrees with the
//      package stops the release here - the pipeline never edits the version to fit a tag.
//   2. dotnet test  tests/EmailAI.Tests -c Release          (npm run test)
//   3. dotnet build EmailAI.slnx    -c Release              (npm run build:server)
//   4. Clean desktop/aspnet-publish, then republish the backend self-contained:
//      dotnet publish src/EmailAI.Api -c Release -r win-x64 --self-contained true
//      (a Release publish never carries appsettings.Development.json - verified below)
//   5. Clean desktop/dist, then package the Windows NSIS installer:
//      electron-builder --win nsis                          (npm run dist)
//   6. Scan the published and packaged backend (scripts/verify-secrets.js): no secret value,
//      no development-only appsettings file, no non-placeholder Exchange/AI endpoint.
//   7. Verify the release: exactly one installer named for this version, its SHA-256 matching the
//      checksum file, the version embedded in the installer, the packaged backend being this
//      publish - then generate desktop/dist/RELEASE-NOTES.md for this version from the real Git
//      history (scripts/release-notes.js) and check the notes against the artifact.
//
// desktop/package.json is the single source of truth for the version and for the installer name
// (build.nsis.artifactName, EmailAI-Setup-<version>.exe, x64 only). scripts/release-version.js
// resolves both and verifies the identity above; scripts/verify-release.js exposes the same checks
// as a standalone gate, so a local release and CI (.github/workflows/release.yml) cannot drift.
// =============================================================================

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

// The release identity lives in one module: scripts/release-version.js resolves the version and
// the installer name and performs every identity check (tag, package, installer name, checksum,
// installer metadata). scripts/verify-release.js is the same checks as a standalone gate and
// scripts/release-notes.js generates the release body, so the three can never disagree.
const releaseVersion = require('./release-version');
const releaseNotes = require('./release-notes');

const DESKTOP_DIR = path.resolve(__dirname, '..');
const REPO_ROOT = path.resolve(DESKTOP_DIR, '..');
const PUBLISH_DIR = path.join(DESKTOP_DIR, 'aspnet-publish');
const DIST_DIR = path.join(DESKTOP_DIR, 'dist');

const packageJson = JSON.parse(fs.readFileSync(path.join(DESKTOP_DIR, 'package.json'), 'utf8'));
const startedAt = Date.now();

/** Reads `--name value` from the command line (used only for --tag). */
function argumentValue(name) {
  const index = process.argv.indexOf(name);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : null;
}

// The tag this run releases: an explicit --tag / EMAILAI_RELEASE_TAG, otherwise the tag at HEAD.
// A build on an untagged commit is a local development build and says so; whenever a tag is known
// the tag and the package version must describe the same release, and the tag must point at the
// commit being built - otherwise the pipeline stops here, before anything is compiled.
const suppliedTag = argumentValue('--tag') || process.env.EMAILAI_RELEASE_TAG || null;
const headTag = releaseVersion.tagAtHead(REPO_ROOT);
const releaseTag = suppliedTag || headTag;

let version;
let installerName;
try {
  if (releaseTag) {
    const identity = releaseVersion.resolveVersionIdentity({ desktopDir: DESKTOP_DIR, tag: releaseTag });
    version = identity.version;
    installerName = identity.installer;
  } else {
    version = releaseVersion.packageVersion(DESKTOP_DIR);
    installerName = releaseVersion.installerFileName(DESKTOP_DIR, version);
  }
} catch (error) {
  if (error instanceof releaseVersion.ReleaseVersionError) fail(error.message);
  throw error;
}

// Developer-only configuration that must never appear in a Release publish or in the
// installer: the packaged application runs as Production.
const FORBIDDEN_CONFIG_FILES = ['appsettings.Development.json', 'appsettings.Local.json'];

// ------------------------------------------------------------------ helpers

function time() {
  return new Date().toISOString().replace('T', ' ').slice(0, 19);
}

function bar() {
  console.log('='.repeat(40));
}

function fail(message) {
  console.error('');
  console.error(`[FAIL] ${message}`);
  console.error('       No installer was generated. Fix the error and run npm run release again.');
  process.exit(1);
}

function runNpm(scriptName) {
  const start = Date.now();
  console.log('');
  console.log(`[${time()}] Step: npm run ${scriptName}`);
  const result = spawnSync('npm', ['run', scriptName], {
    cwd: DESKTOP_DIR,
    shell: true, // npm is npm.cmd on Windows
    stdio: 'inherit',
    env: { ...process.env, CSC_IDENTITY_AUTO_DISCOVERY: 'false' },
  });
  const elapsed = `${((Date.now() - start) / 1000).toFixed(1)}s`;
  if (result.error) fail(`${scriptName}: ${result.error.message}`);
  if (result.status !== 0) {
    console.error(`[FAIL] npm run ${scriptName} exited with code ${result.status} after ${elapsed}.`);
    process.exit(1);
  }
  console.log(`[ok]   npm run ${scriptName} succeeded (${elapsed})`);
}

function removeDir(dir, label) {
  if (!fs.existsSync(dir)) return;
  console.log(`[${time()}] Removing stale ${label}: ${dir}`);
  fs.rmSync(dir, { recursive: true, force: true, maxRetries: 3, retryDelay: 250 });
}

// ---------------------------------------------------------------- 1. validate

console.log('');
bar();
console.log(`EmailAI Local Release  v${version}`);
bar();
console.log(`[${time()}] Step 1/7: Validate project layout and toolchain`);

const requiredFiles = [
  ['solution file', path.join(REPO_ROOT, 'EmailAI.slnx')],
  ['API project', path.join(REPO_ROOT, 'src', 'EmailAI.Api', 'EmailAI.Api.csproj')],
  ['test project', path.join(REPO_ROOT, 'tests', 'EmailAI.Tests', 'EmailAI.Tests.csproj')],
  ['Electron shell', path.join(DESKTOP_DIR, 'main.js')],
  ['electron-builder', path.join(DESKTOP_DIR, 'node_modules', 'electron-builder', 'cli.js')],
];
for (const [label, file] of requiredFiles) {
  if (!fs.existsSync(file)) {
    fail(`${label} not found at ${file}${
      label === 'electron-builder' ? ' - run "npm install" in the desktop folder first' : ''}`);
  }
}
for (const scriptName of ['test', 'build:server', 'publish:server', 'dist', 'release']) {
  if (!packageJson.scripts || !packageJson.scripts[scriptName]) {
    fail(`npm script "${scriptName}" is missing from desktop/package.json`);
  }
}
// The version identity (tag = package version = installer name) was resolved above; here it is
// reported, and a known tag must point at the commit being built.
if (releaseTag) {
  try {
    releaseVersion.assertTagPointsAtHead({ repoRoot: REPO_ROOT, tag: releaseTag });
  } catch (error) {
    if (error instanceof releaseVersion.ReleaseVersionError) fail(error.message);
    throw error;
  }

  console.log(`[ok]   version identity: tag ${releaseTag} = desktop/package.json ${version} = ${installerName}`);
  console.log(`[ok]   the tag points at the checked out commit`);
} else {
  console.log(`[info] no release tag: neither --tag/EMAILAI_RELEASE_TAG nor a tag at HEAD, so this is a`);
  console.log(`[info] local development build of version ${version}. The tag/package identity gate runs`);
  console.log('[info] in .github/workflows/release.yml whenever a v*.*.* tag is pushed.');
}

const dotnetVersion = spawnSync('dotnet', ['--version'], { encoding: 'utf8', shell: true });
if (dotnetVersion.error || dotnetVersion.status !== 0) {
  fail('dotnet CLI not found on PATH - install the .NET 10 SDK and try again.');
}
console.log(`[info] .NET SDK   ${dotnetVersion.stdout.trim().split(/\r?\n/)[0]}`);
console.log(`[info] Node.js    ${process.version}`);
console.log(`[info] installer  dist\\${installerName}`);
console.log('[ok]   validation passed');

// ------------------------------------------------------------------ 2. tests

console.log('');
console.log(`[${time()}] Step 2/7: Run the .NET test suite (Release)`);
runNpm('test');

// ---------------------------------------------------------------- 3. build

console.log('');
console.log(`[${time()}] Step 3/7: Build the full solution (Release)`);
runNpm('build:server');

// ------------------------------------------------------------- 4. publish

console.log('');
console.log(`[${time()}] Step 4/7: Clean and publish the self-contained backend (win-x64)`);
removeDir(PUBLISH_DIR, 'aspnet-publish output');
runNpm('publish:server');

const publishedExe = path.join(PUBLISH_DIR, 'EmailAI.Api.exe');
if (!fs.existsSync(publishedExe)) {
  fail(`dotnet publish finished but ${publishedExe} was not produced.`);
}
console.log(`[info] fresh publish verified: ${publishedExe}`);
console.log(`[info] publish output: ${PUBLISH_DIR} (${fs.readdirSync(PUBLISH_DIR).length} top-level items)`);

// A Release publish must not contain developer-only configuration. The exclusion lives in
// EmailAI.Api.csproj (<Content Remove="appsettings.Development.json" /> for Release); it is
// re-checked here because a development appsettings.json inside the installer is a release
// defect even when its values are harmless.
for (const name of FORBIDDEN_CONFIG_FILES) {
  const stale = path.join(PUBLISH_DIR, name);
  if (fs.existsSync(stale)) {
    fail(`the Release publish contains ${name} (${stale}) - development-only configuration must not ship.`);
  }
}
console.log('[ok]   publish contains no development-only appsettings file');

// --------------------------------------------------------------- 5. package

console.log('');
console.log(`[${time()}] Step 5/7: Clean and package the Windows NSIS installer`);
removeDir(DIST_DIR, 'electron-builder output');
runNpm('dist');

// ---------------------------------------------------------- 6. secret scan

console.log('');
console.log(`[${time()}] Step 6/7: Scan the published and packaged backend (secrets + configuration)`);

const verifySecretsScript = path.join(DESKTOP_DIR, 'scripts', 'verify-secrets.js');
const scanRoots = [PUBLISH_DIR];
const stagedServerDir = path.join(DIST_DIR, 'win-unpacked', 'resources', 'server');
if (fs.existsSync(stagedServerDir)) scanRoots.push(stagedServerDir);

const secretScan = spawnSync('node', [verifySecretsScript, ...scanRoots], {
  cwd: DESKTOP_DIR,
  stdio: 'inherit',
  env: { ...process.env },
});
if (secretScan.error) fail(`verify-secrets: ${secretScan.error.message}`);
if (secretScan.status !== 0) {
  fail('verify-secrets reported packaged secrets - refusing to ship this build.');
}
console.log('[ok]   packaged backend is clean of secrets, development config and non-placeholder endpoints');

// --------------------------------------------------- 7. verify and summarize

console.log('');
console.log(`[${time()}] Step 7/7: Verify the release (artifact, checksum, installer version, notes)`);

const installerPath = path.join(DIST_DIR, installerName);
if (!fs.existsSync(installerPath)) {
  const found = fs.existsSync(DIST_DIR)
    ? fs.readdirSync(DIST_DIR).filter((name) => name.toLowerCase().endsWith('.exe'))
    : [];
  fail(`expected installer ${installerPath} was not created${
    found.length ? `; found: ${found.join(', ')}` : ''}`);
}

// Sanity check: the backend staged for the installer is the publish we just made.
const stagedServerExe = path.join(DIST_DIR, 'win-unpacked', 'resources', 'server', 'EmailAI.Api.exe');
const publishedInfo = fs.statSync(publishedExe);
if (!fs.existsSync(stagedServerExe)) {
  fail('win-unpacked\\resources\\server\\EmailAI.Api.exe is missing - the installer must carry the backend.');
} else if (fs.statSync(stagedServerExe).mtimeMs < publishedInfo.mtimeMs) {
  fail('the packaged backend predates the fresh publish - rerun so the installer carries this build.');
} else {
  console.log('[info] packaged backend verified inside win-unpacked\\resources\\server');
}

// The packaged backend must not carry developer-only configuration either - the guard above
// only proves the intermediate publish output.
for (const name of FORBIDDEN_CONFIG_FILES) {
  const staged = path.join(DIST_DIR, 'win-unpacked', 'resources', 'server', name);
  if (fs.existsSync(staged)) {
    fail(`the packaged backend contains ${name} - development-only configuration must not ship.`);
  }
}
console.log('[ok]   packaged backend contains no development-only configuration');

// SHA-256 of the exact installer file that will be published, written next to it so a download can
// be verified without trusting the transfer. The gate below re-reads both files and compares them.
const sha256 = releaseVersion.sha256(installerPath);
const checksumPath = `${installerPath}.sha256`;
fs.writeFileSync(checksumPath, `${sha256}  ${installerName}\r\n`);
console.log(`[info] SHA-256 written to ${checksumPath}`);

// The release notes are generated from this release's identity, the real Git history since the
// previous release tag and the SHA-256 of this exact installer file - never carried over from an
// earlier release. On an untagged local build they are labelled with the tag they are destined for.
const notesTag = releaseTag || `v${version}`;
let notes = null;
let verified = null;
try {
  notes = releaseNotes.writeReleaseNotes({
    desktopDir: DESKTOP_DIR,
    repoRoot: REPO_ROOT,
    tag: notesTag,
    installerPath,
  });
  console.log(
    `[ok]   release notes generated for EmailAI ${notes.version}: ${notes.path} ` +
    `(${notes.changes.length} change(s) since ${notes.previousTag || 'the start of the project'})`);

  // The whole release contract in one pass: exactly one installer, its checksum file matching that
  // exact file, the version embedded in the installer and notes describing this version and file.
  verified = releaseVersion.verifyInstaller({
    desktopDir: DESKTOP_DIR,
    version,
    tag: notesTag,
    distDir: DIST_DIR,
    notesPath: notes.path,
  });
} catch (error) {
  if (error instanceof releaseVersion.ReleaseVersionError) fail(error.message);
  throw error;
}

const installerBytes = verified.size;
const sizeMb = (installerBytes / (1024 * 1024)).toFixed(1);
console.log(`[ok]   installer verified: ${verified.installer} (${sizeMb} MB), SHA-256 matches its .sha256 file`);
console.log(
  `[ok]   installer metadata: FileVersion=${verified.metadata.fileVersion} ` +
  `ProductVersion=${verified.metadata.productVersion} (version ${version})`);

const totalSeconds = ((Date.now() - startedAt) / 1000).toFixed(1);
console.log('');
bar();
console.log('EmailAI Release Completed');
bar();
console.log(`Version:    ${version}`);
console.log(`Tag:        ${releaseTag || `${notesTag} (not yet created in this checkout)`}`);
console.log('Installer:');
console.log(installerPath);
console.log(`Size:       ${sizeMb} MB (${installerBytes.toLocaleString('en-US')} bytes)`);
console.log(`SHA-256:    ${verified.sha256}`);
console.log(`Checksum:   ${checksumPath}`);
console.log(`Notes:      ${notes.path}`);
console.log(`Total time: ${totalSeconds}s`);
bar();

