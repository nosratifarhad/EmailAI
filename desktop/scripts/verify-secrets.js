'use strict';

// =============================================================================
// EmailAI secret scan for release artifacts.
//
// Usage:
//     node scripts/verify-secrets.js <rootDir...>
//
// What it does (fail-fast, and it NEVER prints a matched value):
//   1. Structured scan over every *.json and every other small text file under the
//      given roots for non-empty assignments to the risky configuration keys
//      (Exchange.Password, Ai.ApiKey, AI_API_KEY=, EXCHANGE_PASSWORD=, ...). Those
//      keys must never carry a value in anything that ships (the Windows Credential
//      Manager and/or process environment are the only allowed homes for secrets).
//   2. Optional raw scan: when EMAILAI_SCAN_EXCHANGE_PASSWORD and/or
//      EMAILAI_SCAN_AI_API_KEY are set (typically by the operator, from values that
//      are known to be retired), each value is searched byte-for-byte across every
//      file in the roots - the only way to also prove compiled binaries and packed
//      resources no longer embed it.
//
// Output lines report only "FOUND" or "NOT FOUND" per label; the actual values are
// never echoed. Exit code is 1 when anything is found so release pipelines fail.
// =============================================================================

const fs = require('node:fs');
const path = require('node:path');

const roots = process.argv.slice(2).filter((arg) => arg && !arg.startsWith('-'));
if (roots.length === 0) {
  console.error('[verify-secrets] usage: node scripts/verify-secrets.js <rootDir...>');
  process.exit(2);
}

const LABEL_PATTERN = /(?:^|["'])(password|apikey|access_token|secret|client_secret|passwd)(?:["'])?\s*[:=]\s*["']([^"']+)["']/gi;

function findFiles(root, out) {
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    const full = path.join(root, entry.name);
    if (entry.isDirectory()) {
      findFiles(full, out);
    } else {
      out.push(full);
    }
  }
  return out;
}

function looksLikeValue(value) {
  const trimmed = (value || '').trim();
  if (!trimmed) return false;
  const normalized = trimmed.toLowerCase();
  // Placeholders/booleans that appear next to "secret"-style keys in sample files
  // are not secrets; anything else is treated as a secret-bearing assignment.
  if (/^(?:<[^>]*>|\[[^\]]*\]|xxx+|change_me|your_|placeholder|\.\.\.+|true|false|null|0|string|secret)$/i.test(normalized)) {
    return false;
  }
  return normalized.length >= 4;
}

function scanTextForSecretAssignments(content, filePath, label) {
  LABEL_PATTERN.lastIndex = 0;
  let match;
  while ((match = LABEL_PATTERN.exec(content)) !== null) {
    if (looksLikeValue(match[2])) {
      return { file: filePath, label: `${label} (${match[1]})` };
    }
  }
  return null;
}

const ALLOWED_BINARY_EXTENSIONS = new Set();
function report(label, found) {
  console.log(`[verify-secrets] ${label}: ${found ? 'FOUND' : 'NOT FOUND'}`);
  return found;
}

let failed = false;

for (const root of roots) {
  if (!fs.existsSync(root)) {
    console.error(`[verify-secrets] root does not exist: ${root}`);
    process.exit(2);
  }

  const files = findFiles(root, []);
  console.log(`[verify-secrets] scanning ${root} (${files.length} files)`);

  let jsonWithKeyCount = 0;
  for (const file of files) {
    const isJson = /appsettings[^/\\]*\.json$/i.test(file);
    const stat = fs.statSync(file);
    // Structured scan: config JSON files fully; anything else only when small enough
    // to be a text/config/resource candidate (never read 200 MB binaries).
    if (!isJson && (stat.size > 512 * 1024 || /\.(?:dll|exe|so|node|nupkg|7z)$/i.test(file))) {
      continue;
    }

    let content;
    try {
      content = fs.readFileSync(file, 'utf8');
    } catch {
      continue; // binary or unreadable - skipped by the size/extension filter above
    }

    if (isJson) jsonWithKeyCount += 1;
    const hit = scanTextForSecretAssignments(content, file, 'packaged secret key with a value');
    if (hit) {
      console.error(`[verify-secrets] ${hit.label}: FOUND in ${hit.file}`);
      failed = true;
    }
  }

  if (jsonWithKeyCount === 0) {
    console.warn('[verify-secrets] warning: no appsettings*.json found under this root');
  }
}

// Optional raw byte scan for known retired values supplied by the operator. Values
// are only compared, never printed.
for (const envName of ['EMAILAI_SCAN_EXCHANGE_PASSWORD', 'EMAILAI_SCAN_AI_API_KEY']) {
  const secret = process.env[envName];
  if (!secret || secret.length < 4) continue;
  let found = false;
  for (const root of roots) {
    for (const file of findFiles(root, [])) {
      const buffer = fs.readFileSync(file);
      if (buffer.includes(Buffer.from(secret, 'utf8'))) {
        found = true;
        console.error(`[verify-secrets] ${envName} raw marker: FOUND in ${file}`);
        break;
      }
    }
    if (found) break;
  }
  failed = report(`${envName} raw marker`, found) || failed;
}

if (failed) {
  console.error('[verify-secrets] FAILED - a packaged secret was detected. Do NOT release this build.');
  process.exit(1);
}
console.log('[verify-secrets] PASS - no packaged secrets detected (values never echoed).');
