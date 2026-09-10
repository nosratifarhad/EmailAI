'use strict';

// =============================================================================
// EmailAI local Windows release - one command:
//
//     cd "D:\New folder (2)\desktop"
//     npm run release
//
// Pipeline (fail-fast - if any step fails, no installer is produced):
//   1. Validate the project layout and the required toolchain.
//   2. dotnet test  tests/EmailAI.Tests -c Release          (npm run test)
//   3. dotnet build EmailAI.slnx    -c Release              (npm run build:server)
//   4. Clean desktop/aspnet-publish, then republish the backend self-contained:
//      dotnet publish src/EmailAI.Api -c Release -r win-x64 --self-contained true
//   5. Clean desktop/dist, then package the Windows NSIS installer:
//      electron-builder --win nsis                          (npm run dist)
//   6. Secret-scan the published and packaged backend (scripts/verify-secrets.js).
//   7. Verify desktop/dist/EmailAI-Setup-<version>.exe and print a summary.
//
// desktop/package.json "version" is the single source of truth: electron-builder
// already derives the installer name from it (EmailAI-Setup-${version}.exe), so a
// release is simply "bump version in desktop/package.json, then npm run release".
// =============================================================================

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const DESKTOP_DIR = path.resolve(__dirname, '..');
const REPO_ROOT = path.resolve(DESKTOP_DIR, '..');
const PUBLISH_DIR = path.join(DESKTOP_DIR, 'aspnet-publish');
const DIST_DIR = path.join(DESKTOP_DIR, 'dist');

const packageJson = JSON.parse(fs.readFileSync(path.join(DESKTOP_DIR, 'package.json'), 'utf8'));
const version = packageJson.version;
const startedAt = Date.now();

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
if (!/^\d+\.\d+\.\d+/.test(version)) {
  fail(`package.json "version" is not a simple semver (got "${version}").`);
}

const dotnetVersion = spawnSync('dotnet', ['--version'], { encoding: 'utf8', shell: true });
if (dotnetVersion.error || dotnetVersion.status !== 0) {
  fail('dotnet CLI not found on PATH - install the .NET 10 SDK and try again.');
}
console.log(`[info] .NET SDK   ${dotnetVersion.stdout.trim().split(/\r?\n/)[0]}`);
console.log(`[info] Node.js    ${process.version}`);
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

// --------------------------------------------------------------- 5. package

console.log('');
console.log(`[${time()}] Step 5/7: Clean and package the Windows NSIS installer`);
removeDir(DIST_DIR, 'electron-builder output');
runNpm('dist');

// ---------------------------------------------------------- 6. secret scan

console.log('');
console.log(`[${time()}] Step 6/7: Secret-scan the published and packaged backend`);

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
console.log('[ok]   packaged backend is clean of secrets');

// --------------------------------------------------- 7. verify and summarize

console.log('');
console.log(`[${time()}] Step 7/7: Verify the generated installer`);

const installerPath = path.join(DIST_DIR, `EmailAI-Setup-${version}.exe`);
if (!fs.existsSync(installerPath)) {
  const found = fs.existsSync(DIST_DIR)
    ? fs.readdirSync(DIST_DIR).filter((name) => /^EmailAI-Setup-.*\.exe$/.test(name))
    : [];
  fail(`expected installer ${installerPath} was not created${
    found.length ? `; found: ${found.join(', ')}` : ''}`);
}
const sizeMb = (fs.statSync(installerPath).size / (1024 * 1024)).toFixed(1);

// Sanity check: the backend staged for the installer is the publish we just made.
const stagedServerExe = path.join(DIST_DIR, 'win-unpacked', 'resources', 'server', 'EmailAI.Api.exe');
const publishedInfo = fs.statSync(publishedExe);
if (!fs.existsSync(stagedServerExe)) {
  console.warn('[warn] win-unpacked\\resources\\server\\EmailAI.Api.exe not found (installer still produced).');
} else if (fs.statSync(stagedServerExe).mtimeMs < publishedInfo.mtimeMs) {
  fail('the packaged backend predates the fresh publish - rerun so the installer carries this build.');
} else {
  console.log('[info] packaged backend verified inside win-unpacked\\resources\\server');
}

const totalSeconds = ((Date.now() - startedAt) / 1000).toFixed(1);
console.log('');
bar();
console.log('EmailAI Release Completed');
bar();
console.log(`Version:    ${version}`);
console.log('Installer:');
console.log(installerPath);
console.log(`Size:       ${sizeMb} MB`);
console.log(`Total time: ${totalSeconds}s`);
bar();
