'use strict';

// =============================================================================
// The EmailAI release gate.
//
//   node scripts/verify-release.js --tag vX.Y.Z
//       The identity gate. The tag, desktop/package.json and desktop/package-lock.json must
//       describe one version. `.github/workflows/release.yml` runs this BEFORE anything is built,
//       so a tag that disagrees with the package stops the run before a byte is packaged.
//
//   node scripts/verify-release.js --tag vX.Y.Z --dist dist --notes dist/RELEASE-NOTES.md
//       The artifact gate, after packaging: exactly one installer carrying that version, its
//       `.sha256` holding the SHA-256 of that exact file, the version embedded in the installer,
//       and release notes describing the same version, installer and checksum.
//
// Options:
//   --tag <tag>                 the release tag (or the EMAILAI_RELEASE_TAG environment variable)
//   --desktop <dir>             the folder holding package.json (default: the scripts' parent)
//   --dist <dir>                verify the packaging output in this folder (default: skipped)
//   --notes <file>              verify these generated release notes (default: none)
//   --no-metadata               skip the installer version metadata check (tests only)
//   --allow-tag-not-at-head     do not require the tag to point at HEAD
//   --github-output <file>      append tag=/version=/installer=/checksum= for a workflow step
//   --help
//
// Exit code 0 = every check passed. Exit code 1 = the release must not continue, and the message
// states the expected value, the actual value and where each came from. The checks themselves live
// in scripts/release-version.js, which scripts/release.js also calls, so a local release and this
// gate can never disagree.
// =============================================================================

const fs = require('node:fs');
const path = require('node:path');
const releaseVersion = require('./release-version');

const DESKTOP_DIR = path.resolve(__dirname, '..');
const REPO_ROOT = path.resolve(DESKTOP_DIR, '..');

function usage() {
  return [
    'EmailAI release gate',
    '',
    '  node scripts/verify-release.js --tag vX.Y.Z [--dist <dir>] [--notes <file>]',
    '',
    '  --tag <tag>              the release tag (or EMAILAI_RELEASE_TAG)',
    '  --desktop <dir>          the folder holding package.json',
    '  --dist <dir>             verify the packaged output (installer, checksum, metadata)',
    '  --notes <file>           verify generated release notes as well',
    '  --no-metadata            skip the installer version metadata check (tests only)',
    '  --allow-tag-not-at-head  do not require the release tag to point at HEAD',
    '  --github-output <file>   append tag=/version=/installer=/checksum= for a workflow step',
    '  --help',
  ].join('\n');
}

function parseArguments(argv) {
  const options = {
    desktop: DESKTOP_DIR,
    dist: null,
    notes: null,
    tag: process.env.EMAILAI_RELEASE_TAG || null,
    requireMetadata: true,
    allowTagNotAtHead: false,
    githubOutput: null,
  };

  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    const value = (name) => {
      const next = argv[++index];
      if (typeof next !== 'string' || next.length === 0) {
        throw new releaseVersion.ReleaseVersionError(`${name} needs a value.`);
      }

      return next;
    };

    switch (argument) {
      case '--tag': options.tag = value(argument); break;
      case '--desktop': options.desktop = path.resolve(value(argument)); break;
      case '--dist': options.dist = path.resolve(value(argument)); break;
      case '--notes': options.notes = path.resolve(value(argument)); break;
      case '--no-metadata': options.requireMetadata = false; break;
      case '--allow-tag-not-at-head': options.allowTagNotAtHead = true; break;
      case '--github-output': options.githubOutput = value(argument); break;
      case '--help': options.help = true; break;
      default:
        throw new releaseVersion.ReleaseVersionError(
          `unknown argument "${argument}".\n\n${usage()}`);
    }
  }

  return options;
}

function main(argv) {
  const options = parseArguments(argv);
  if (options.help) {
    console.log(usage());
    return 0;
  }

  const identity = releaseVersion.resolveVersionIdentity({
    desktopDir: options.desktop,
    tag: options.tag,
  });

  const lock = releaseVersion.packageLockVersions(options.desktop);
  const lockVersion = lock && lock.declared ? lock.declared : identity.version;
  const commitCheck = options.allowTagNotAtHead
    ? { checked: false, reason: 'the release-tag/commit check was disabled (--allow-tag-not-at-head)' }
    : releaseVersion.assertTagPointsAtHead({ repoRoot: REPO_ROOT, tag: identity.tag });

  console.log('EmailAI release gate');
  console.log(`  tag        ${identity.tag}`);
  console.log(`  version    ${identity.version}`);
  console.log(`  installer  ${identity.installer}`);
  console.log(`  checksum   ${identity.checksum}`);
  console.log('');
  console.log(
    `[ok]   version identity: tag ${identity.tag} = desktop/package.json ${identity.version} ` +
    `= desktop/package-lock.json ${lockVersion}`);

  if (commitCheck.checked) {
    console.log(`[ok]   the tag points at the checked out commit (${commitCheck.commit.slice(0, 12)})`);
  } else {
    console.log(`[info] ${commitCheck.reason}`);
  }

  if (options.dist) {
    const verified = releaseVersion.verifyInstaller({
      desktopDir: options.desktop,
      version: identity.version,
      tag: identity.tag,
      distDir: options.dist,
      notesPath: options.notes,
      requireMetadata: options.requireMetadata,
    });

    const sizeMb = (verified.size / (1024 * 1024)).toFixed(1);
    console.log(`[ok]   artifact: ${verified.installer} (${sizeMb} MB; exactly one installer in the output folder)`);
    console.log(`[ok]   checksum: sha256 = ${verified.sha256} matches ${verified.checksum}`);

    if (verified.metadata) {
      console.log(
        `[ok]   installer metadata: FileVersion=${verified.metadata.fileVersion} ` +
        `ProductVersion=${verified.metadata.productVersion}`);
    } else {
      console.log('[info] the installer version metadata check was disabled (--no-metadata)');
    }

    if (options.notes) {
      console.log(`[ok]   release notes: they describe EmailAI ${identity.version} and ${verified.installer}`);
    }
  } else if (options.notes) {
    if (!fs.existsSync(options.notes)) {
      throw new releaseVersion.ReleaseVersionError(`the release notes ${options.notes} do not exist.`);
    }

    releaseVersion.assertReleaseNotes(fs.readFileSync(options.notes, 'utf8'), {
      version: identity.version,
      label: options.notes,
    });
    console.log(`[ok]   release notes: they describe EmailAI ${identity.version}`);
  } else {
    console.log('[info] no packaging output was requested (--dist): only the version identity was verified');
  }

  if (options.githubOutput) {
    fs.appendFileSync(
      options.githubOutput,
      `tag=${identity.tag}\n` +
      `version=${identity.version}\n` +
      `installer=${identity.installer}\n` +
      `checksum=${identity.checksum}\n`);
  }

  console.log('');
  console.log(`PASS: release gate - ${identity.tag} / ${identity.installer}`);
  return 0;
}

try {
  process.exitCode = main(process.argv.slice(2));
} catch (error) {
  if (error instanceof releaseVersion.ReleaseVersionError) {
    console.error('');
    console.error(`[FAIL] ${error.message}`);
    console.error('');
    console.error('FAIL: release gate - the release must not continue.');
    process.exitCode = 1;
  } else {
    throw error;
  }
}
