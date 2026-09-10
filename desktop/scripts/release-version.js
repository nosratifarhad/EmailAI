'use strict';

// =============================================================================
// The release version identity - resolved once, verified everywhere.
//
// `desktop/package.json` `"version"` is the authoritative application version, and a release is
// identified by the Git tag `v<version>`. This module is the single place that:
//
//   * reads the version (and the installer-name template) out of `desktop/package.json`,
//   * turns a Git tag into a version - and refuses a tag that disagrees with the package,
//   * derives the installer and checksum file names from that version,
//   * verifies a produced installer: its file name, its SHA-256 checksum file and the version
//     metadata electron-builder embeds in the Windows installer, and
//   * verifies that release notes describe exactly the version being released.
//
// Two rules make this structurally resistant to the defect class "the release announces one version
// and ships another":
//
//   1. The version is read from `desktop/package.json` and nowhere else. Nothing in this module
//      falls back to a literal, an environment variable, a previously published artifact or a
//      build directory - so no release can silently carry another version's name.
//   2. Every disagreement is a hard failure. The pipeline (`scripts/release.js`), the standalone
//      gate (`scripts/verify-release.js`) and `.github/workflows/release.yml` all call this
//      module, so a local release and CI cannot drift apart.
//
// This file deliberately contains no version literal; `tests/EmailAI.Tests/ReleaseVersionTests.cs`
// fails if one appears here (or in the other release scripts).
// =============================================================================

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');

const SEMVER_PATTERN = /^\d+\.\d+\.\d+$/;
const RELEASE_TAG_PATTERN = /^v(\d+\.\d+\.\d+)$/;
const ARTIFACT_VERSION_TOKEN = '${version}';
const CHECKSUM_LINE_PATTERN = /^([0-9a-fA-F]{64})\s+\*?(.+)$/;

/** A release-integrity failure. The message is the report a maintainer has to act on. */
class ReleaseVersionError extends Error {
  constructor(message) {
    super(message);
    this.name = 'ReleaseVersionError';
  }
}

function fail(message) {
  throw new ReleaseVersionError(message);
}

// --------------------------------------------------------------- package.json

function readJson(file, label) {
  if (!fs.existsSync(file)) {
    fail(`${label} was not found at ${file}.`);
  }

  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (error) {
    fail(`${label} is not valid JSON: ${error.message}`);
  }
}

function readPackage(desktopDir) {
  return readJson(path.join(desktopDir, 'package.json'), 'desktop/package.json');
}

/** The authoritative application version. Read from `desktop/package.json`, never elsewhere. */
function packageVersion(desktopDir) {
  const version = readPackage(desktopDir).version;
  if (typeof version !== 'string' || !SEMVER_PATTERN.test(version)) {
    fail(
      'desktop/package.json "version" must be a simple semantic version (X.Y.Z) - it is the ' +
      `authoritative application version (got ${JSON.stringify(version)}).`);
  }

  return version;
}

/**
 * The version recorded in `desktop/package-lock.json` (both the top-level field and the root
 * package entry). Both must equal `package.json`'s version, or npm would rewrite the package the
 * next time anyone runs `npm install`.
 */
function packageLockVersions(desktopDir) {
  const lockFile = path.join(desktopDir, 'package-lock.json');
  if (!fs.existsSync(lockFile)) {
    return null;
  }

  const lock = readJson(lockFile, 'desktop/package-lock.json');
  const root = lock.packages && lock.packages[''] ? lock.packages[''].version : null;
  return { declared: typeof lock.version === 'string' ? lock.version : null, root };
}

// -------------------------------------------------------------------- version

/** `vX.Y.Z` -> `{ tag, version }`; anything else is a hard failure (never a fallback). */
function normalizeTag(tag) {
  const value = typeof tag === 'string' ? tag.trim() : '';
  if (value.length === 0) {
    fail([
      'no release tag was supplied.',
      '',
      'A release is identified by its tag, and that tag must equal desktop/package.json "version"',
      '(tag vX.Y.Z  ->  version X.Y.Z). Supply it as one of:',
      '  * the --tag argument,',
      '  * the EMAILAI_RELEASE_TAG environment variable, or',
      '  * a checkout whose HEAD is exactly tagged (git describe --tags --exact-match).',
    ].join('\n'));
  }

  const match = RELEASE_TAG_PATTERN.exec(value);
  if (!match) {
    fail([
      `"${value}" is not a release tag.`,
      '',
      'A release tag is "v" followed by the semantic version that desktop/package.json declares',
      '(for example vX.Y.Z) - a branch name or a partial version can never identify a release.',
    ].join('\n'));
  }

  return { tag: `v${match[1]}`, version: match[1] };
}

/** The installer name template from `desktop/package.json` (`build.nsis.artifactName`). */
function artifactTemplate(desktopDir) {
  const build = readPackage(desktopDir).build;
  const template = build && build.nsis ? build.nsis.artifactName : null;

  if (typeof template !== 'string' || !template.includes(ARTIFACT_VERSION_TOKEN)) {
    fail(
      'desktop/package.json build.nsis.artifactName must contain "' + ARTIFACT_VERSION_TOKEN +
      '" - it names the installer (got ' + JSON.stringify(template) + ').');
  }

  if (/^[A-Za-z]:[\\/]|^\//.test(template)) {
    fail(`build.nsis.artifactName must be a file name, not a path (got "${template}").`);
  }

  return template;
}

/**
 * The installer file name for a version. electron-builder derives the real file name from this
 * same template, so the pipeline reads it instead of duplicating a name (or a version).
 */
function installerFileName(desktopDir, version) {
  return artifactTemplate(desktopDir).replace(ARTIFACT_VERSION_TOKEN, version);
}

/**
 * The release identity: the tag, the package version, the installer name and the checksum name
 * must describe one version. A disagreement stops the release here - the pipeline never bumps the
 * version to fit a tag.
 */
function resolveVersionIdentity({ desktopDir, tag }) {
  const version = packageVersion(desktopDir);
  const normalized = normalizeTag(tag);
  const lock = packageLockVersions(desktopDir);
  const expectedTag = `v${version}`;
  const problems = [];

  if (normalized.tag !== expectedTag) {
    problems.push(`the tag names version ${normalized.version}, the package declares ${version}`);
  }

  if (lock) {
    if (lock.declared && lock.declared !== version) {
      problems.push(`desktop/package-lock.json "version" is ${lock.declared}`);
    }

    if (lock.root && lock.root !== version) {
      problems.push(`desktop/package-lock.json packages[""] "version" is ${lock.root}`);
    }
  }

  if (problems.length > 0) {
    const sources = [
      '  expected version   ' + normalized.version + '   (source: Git tag ' + normalized.tag + ')',
      '  actual version     ' + version + '   (source: desktop/package.json "version")',
    ];

    if (lock && lock.declared) {
      sources.push('  lockfile version   ' + lock.declared + '   (source: desktop/package-lock.json)');
    }

    fail([
      'release version mismatch - the release identity is not coherent, so nothing was packaged.',
      '',
      ...sources,
      '',
      '  ' + problems.join('\n  '),
      '',
      'Fix the configuration - exactly one of these, and never by renaming an artifact, overwriting',
      'a published tag or reusing a previous installer:',
      '  * to release version ' + normalized.version + ', set desktop/package.json "version" to ' +
        normalized.version + ' (and its lock file),',
      '    land it through a pull request against main, then push the tag ' + normalized.tag + '; or',
      '  * push the tag that matches the package: ' + expectedTag + '.',
      '',
      'The pipeline never changes the version to fit a tag: a tag that disagrees with the package',
      'is a release configuration error, and the release stops before anything is built.',
    ].join('\n'));
  }

  return {
    version,
    tag: normalized.tag,
    installer: installerFileName(desktopDir, version),
    checksum: `${installerFileName(desktopDir, version)}.sha256`,
  };
}

// ----------------------------------------------------- the published artifacts

function sha256(file) {
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

/** Reads the `<sha256>  <file name>` line a checksum file must contain. */
function parseChecksumFile(text, file) {
  const line = text.split(/\r?\n/).map((value) => value.trim()).find((value) => value.length > 0);
  if (!line) {
    fail(`the checksum file ${file} is empty.`);
  }

  const match = CHECKSUM_LINE_PATTERN.exec(line);
  if (!match) {
    fail(`the checksum file ${file} must contain one "<sha256>  <file name>" line (got "${line}").`);
  }

  return { hash: match[1].toLowerCase(), name: match[2].trim() };
}

/**
 * The version electron-builder embedded in the installer (Windows only).
 *
 * electron-builder writes the `package.json` version into the NSIS installer's version resource,
 * so `FileVersion`/`ProductVersion` of the produced file prove which version was really packaged -
 * the file name alone does not.
 */
function installerVersionMetadata(installer) {
  if (process.platform !== 'win32') {
    return null;
  }

  const literal = installer.replace(/'/g, "''");
  const command =
    `(Get-Item -LiteralPath '${literal}').VersionInfo | ` +
    'Select-Object FileVersion,ProductVersion | ConvertTo-Json -Compress';
  const result = spawnSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', command], {
    encoding: 'utf8',
  });
  if (result.error || result.status !== 0 || !result.stdout) {
    return null;
  }

  try {
    const parsed = JSON.parse(result.stdout.trim());
    return {
      fileVersion: parsed.FileVersion || null,
      productVersion: parsed.ProductVersion || null,
    };
  } catch (error) {
    return null;
  }
}

/** The version resource may be recorded as `X.Y.Z` or `X.Y.Z.0`. */
function versionMatches(recorded, expected) {
  if (typeof recorded !== 'string') {
    return false;
  }

  const value = recorded.trim();
  return value === expected || value === `${expected}.0`;
}

/**
 * Release notes must describe the version being released - this is the check that catches a
 * release whose notes were carried over from an earlier version.
 */
function assertReleaseNotes(text, { version, installer, checksum, label }) {
  const named = [...text.matchAll(/EmailAI\s+(\d+\.\d+\.\d+)/g)].map((match) => match[1]);
  if (named.length === 0) {
    fail(`${label} does not name the release version: expected "EmailAI ${version}".`);
  }

  const wrong = [...new Set(named.filter((value) => value !== version))];
  if (wrong.length > 0) {
    fail([
      `${label} describes the wrong version.`,
      '',
      `  expected  EmailAI ${version}`,
      `  found     EmailAI ${wrong.join(', EmailAI ')}`,
      '',
      'Release notes are generated for the version being released; notes carried over from an',
      'earlier release must never be published again.',
    ].join('\n'));
  }

  if (!text.includes(`# EmailAI ${version}`)) {
    fail(
      `${label} must carry the heading "# EmailAI ${version}" so the release title, the release ` +
      'notes and the installer state the same version.');
  }

  if (installer && !text.includes(installer)) {
    fail(`${label} does not name the installer it releases ("${installer}").`);
  }

  if (checksum && !text.toLowerCase().includes(checksum.toLowerCase())) {
    fail(
      `${label} does not contain the checksum of the published installer (${checksum}): a ` +
      'checksum in the release notes must be the checksum of the exact published file.');
  }
}

// ---------------------------------------------------------------- git helpers

function git(args, repoRoot) {
  const result = spawnSync('git', args, { cwd: repoRoot, encoding: 'utf8' });
  if (result.error || result.status !== 0) {
    return null;
  }

  return (result.stdout || '').trim();
}

/** The release tag at HEAD, when the checkout is exactly a release tag. */
function tagAtHead(repoRoot) {
  const tag = git(['describe', '--tags', '--exact-match', '--match', 'v*.*.*', 'HEAD'], repoRoot);
  return tag && RELEASE_TAG_PATTERN.test(tag) ? tag : null;
}

/**
 * The tag a run is releasing: an explicitly supplied tag wins (the CLI argument or
 * EMAILAI_RELEASE_TAG), otherwise the tag at HEAD. Never a value derived from the version.
 */
function intendedTag({ repoRoot, explicitTag }) {
  if (typeof explicitTag === 'string' && explicitTag.trim().length > 0) {
    return { tag: explicitTag.trim(), source: 'the supplied tag' };
  }

  const head = tagAtHead(repoRoot);
  return head
    ? { tag: head, source: 'the tag at HEAD (git describe --tags --exact-match)' }
    : { tag: null, source: null };
}

/** A release must be built from the commit its tag points at. */
function assertTagPointsAtHead({ repoRoot, tag }) {
  const tagCommit = git(['rev-parse', `${tag}^{commit}`], repoRoot);
  if (!tagCommit) {
    return { checked: false, reason: `the tag ${tag} does not exist in this checkout yet` };
  }

  const head = git(['rev-parse', 'HEAD'], repoRoot);
  if (head && head !== tagCommit) {
    fail([
      `the tag ${tag} points at commit ${tagCommit.slice(0, 12)}, but this checkout is at ${head.slice(0, 12)}.`,
      '',
      'A release must be built from the tagged commit: check out the tag (or the commit it points',
      'at) and run the pipeline again. Publishing an installer that was not built from the tag is',
      'how a release ends up carrying another version.',
    ].join('\n'));
  }

  return { checked: true, commit: tagCommit };
}

// ------------------------------------------------------------- the release gate

/**
 * Verify the produced release:
 *
 *   * exactly one installer exists and carries the release version in its name,
 *   * its `.sha256` file exists, names that file and holds that file's real SHA-256,
 *   * the version embedded in the installer equals the release version, and
 *   * the release notes describe the same version, the same installer and the same checksum.
 *
 * `requireMetadata: false` is only for tests that exercise the name/checksum rules with a
 * placeholder file; the pipeline and the workflow always read the real installer metadata.
 */
function verifyInstaller({ desktopDir, version, tag, distDir, notesPath, requireMetadata = true }) {
  const installer = installerFileName(desktopDir, version);
  const checksum = `${installer}.sha256`;

  if (!fs.existsSync(distDir)) {
    fail(`the packaging output ${distDir} does not exist: package the release before verifying it.`);
  }

  const produced = fs.readdirSync(distDir).filter((name) => name.toLowerCase().endsWith('.exe'));
  if (produced.length === 0) {
    fail(`no installer was produced in ${distDir}: expected exactly "${installer}".`);
  }

  const unexpected = produced.filter((name) => name !== installer);
  if (unexpected.length > 0) {
    fail([
      `release ${tag} (version ${version}) must publish exactly one installer: "${installer}".`,
      '',
      `  expected  ${installer}`,
      `  found     ${produced.join(', ')}`,
      '',
      'A differently named installer is stale or belongs to another version. It is never renamed to',
      'fit a release and never uploaded: fix the version/packaging configuration and rebuild into a',
      'clean output folder.',
    ].join('\n'));
  }

  const installerPath = path.join(distDir, installer);
  const actualSha256 = sha256(installerPath);

  const checksumPath = path.join(distDir, checksum);
  if (!fs.existsSync(checksumPath)) {
    fail(
      `the checksum file "${checksum}" is missing from ${distDir}: the installer must never be ` +
      'published without the checksum of the exact file.');
  }

  const recorded = parseChecksumFile(fs.readFileSync(checksumPath, 'utf8'), checksumPath);
  if (recorded.name !== installer) {
    fail(`the checksum file "${checksum}" names "${recorded.name}" instead of "${installer}".`);
  }

  if (recorded.hash !== actualSha256) {
    fail([
      `checksum mismatch for ${installer}:`,
      '',
      `  sha256(${installer})  ${actualSha256}`,
      `  ${checksum}  ${recorded.hash}`,
      '',
      'The checksum must be calculated from the exact installer file that is published.',
    ].join('\n'));
  }

  let metadata = null;
  if (requireMetadata) {
    metadata = installerVersionMetadata(installerPath);
    if (!metadata || (!metadata.fileVersion && !metadata.productVersion)) {
      fail(
        `no version metadata could be read from "${installer}": a release installer must carry the ` +
        `version it is released as (${version}).`);
    }

    if (!versionMatches(metadata.fileVersion, version) ||
        !versionMatches(metadata.productVersion, version)) {
      fail([
        `installer metadata mismatch for ${installer}:`,
        '',
        `  expected  FileVersion/ProductVersion  ${version}`,
        `  actual    FileVersion=${metadata.fileVersion}  ProductVersion=${metadata.productVersion}`,
        '',
        'The file name, the version embedded in the installer and the package version must be the',
        'same version - a renamed artifact is not a release.',
      ].join('\n'));
    }
  }

  if (notesPath) {
    if (!fs.existsSync(notesPath)) {
      fail(`the release notes ${notesPath} are missing: the release body is generated from them.`);
    }

    assertReleaseNotes(fs.readFileSync(notesPath, 'utf8'), {
      version,
      installer,
      checksum: actualSha256,
      label: notesPath,
    });
  }

  return {
    installer,
    checksum,
    installerPath,
    checksumPath,
    sha256: actualSha256,
    size: fs.statSync(installerPath).size,
    metadata,
  };
}

module.exports = {
  RELEASE_TAG_PATTERN,
  ReleaseVersionError,
  artifactTemplate,
  assertReleaseNotes,
  assertTagPointsAtHead,
  installerFileName,
  installerVersionMetadata,
  intendedTag,
  normalizeTag,
  packageLockVersions,
  packageVersion,
  parseChecksumFile,
  resolveVersionIdentity,
  sha256,
  tagAtHead,
  verifyInstaller,
};





