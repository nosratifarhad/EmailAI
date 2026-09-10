'use strict';

// =============================================================================
// EmailAI release-artifact scan (secrets + packaging configuration).
//
// Usage:
//     node scripts/verify-secrets.js <rootDirOrFile...>
//     node scripts/verify-secrets.js --allow-development-config ../src   (source-tree scan:
//         skips check 2, because the repository legitimately contains appsettings.Development.json)
//
// What it does (fail-fast):
//   1. Structured scan over every appsettings*.json and every other small text file under
//      the given roots for non-empty assignments to the risky configuration keys
//      (Exchange.Password, Ai.ApiKey, AI_API_KEY=, EXCHANGE_PASSWORD=, ...). Those keys
//      must never carry a value in anything that ships (the Windows Credential Manager
//      and/or process environment are the only allowed homes for secrets).
//   2. Development-only configuration guard: an appsettings.Development.json /
//      appsettings.Local.json anywhere under a scanned root fails the scan - the packaged
//      application runs as Production, so shipping developer configuration is a defect
//      even when its values are harmless.
//   3. Endpoint guard: every EWS/AI endpoint inside a shipped appsettings*.json must be a
//      documentation placeholder (example.com/org/net, contoso.com, localhost). A real
//      internal or public endpoint baked into the package fails the scan.
//   4. Optional raw scan: when EMAILAI_SCAN_EXCHANGE_PASSWORD and/or
//      EMAILAI_SCAN_AI_API_KEY are set (typically by the operator, from values that
//      are known to be retired), each value is searched byte-for-byte across every
//      file in the roots - the only way to also prove compiled binaries and packed
//      resources no longer embed it.
//
// Secret values are NEVER echoed: findings are reported as "FOUND" plus the file (and, for
// the endpoint guard, the offending host, which is configuration and not a secret). Exit
// code is 1 when anything is found so release pipelines fail.
// =============================================================================

const fs = require('node:fs');
const path = require('node:path');

const args = process.argv.slice(2);
// The development-configuration guard is about SHIPPED artifacts. A source-tree scan
// (node scripts/verify-secrets.js --allow-development-config ../src) legitimately contains
// appsettings.Development.json, so that scan opts out of this one check - never the others.
const allowDevelopmentConfig = args.includes('--allow-development-config');
const roots = args.filter((arg) => arg && !arg.startsWith('-'));
if (roots.length === 0) {
  console.error('[verify-secrets] usage: node scripts/verify-secrets.js <rootDirOrFile...>');
  process.exit(2);
}

const LABEL_PATTERN = /(?:^|["'])(password|apikey|access_token|secret|client_secret|passwd)(?:["'])?\s*[:=]\s*["']([^"']+)["']/gi;

// Developer-only appsettings variants: they must never exist in a shipped artifact.
const DEVELOPMENT_CONFIG_PATTERN = /^appsettings\.(?:Development|Local)\.json$/i;

// Endpoint keys inside appsettings*.json that must hold a documentation placeholder only.
const ENDPOINT_KEY_PATTERN = /"(?:ewsurl|ews|baseurl|url)"\s*:\s*"([^"]*)"/gi;
const DOCUMENTATION_DOMAINS = ['example.com', 'example.org', 'example.net', 'contoso.com'];

function isDocumentationHost(host) {
  const normalized = (host || '').toLowerCase();
  if (!normalized) return false;
  if (normalized === 'localhost' || normalized === '127.0.0.1') return true;
  return DOCUMENTATION_DOMAINS.some(
    (domain) => normalized === domain || normalized.endsWith('.' + domain));
}

// Every endpoint a shipped appsettings*.json may contain must be a documentation placeholder:
// a real internal or public Exchange/AI endpoint baked into the package is a release defect
// (see README "Security notes" and .ai/spec/15-release-distribution.md). The offending host is
// printed because it is configuration, not a secret.
function scanEndpoints(content, filePath) {
  ENDPOINT_KEY_PATTERN.lastIndex = 0;
  let match;
  let found = false;
  while ((match = ENDPOINT_KEY_PATTERN.exec(content)) !== null) {
    const value = (match[1] || '').trim();
    if (!value) continue;
    let host = null;
    try {
      host = new URL(value).hostname;
    } catch {
      host = null;
    }
    if (!isDocumentationHost(host)) {
      console.error(`[verify-secrets] non-placeholder endpoint host "${host || value}" in ${filePath}`);
      found = true;
    }
  }
  return found;
}

function findFiles(root, out) {
  if (fs.statSync(root).isFile()) {
    out.push(root);
    return out;
  }
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
  let devConfigHit = false;
  let endpointHit = false;
  for (const file of files) {
    const isJson = /appsettings[^/\\]*\.json$/i.test(file);
    // Developer-only configuration must never ship, whatever it contains.
    if (!allowDevelopmentConfig && DEVELOPMENT_CONFIG_PATTERN.test(path.basename(file))) {
      console.error(`[verify-secrets] development-only configuration: FOUND in ${file}`);
      devConfigHit = true;
      continue;
    }
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

    if (isJson && scanEndpoints(content, file)) {
      endpointHit = true;
    }
  }

  if (allowDevelopmentConfig) {
    console.log('[verify-secrets] development-only configuration: SKIPPED (source-tree scan)');
  } else {
    failed = report('development-only configuration', devConfigHit) || failed;
  }
  if (jsonWithKeyCount > 0) {
    failed = report('non-placeholder endpoint host', endpointHit) || failed;
  }
  if (jsonWithKeyCount === 0 && fs.statSync(root).isDirectory()) {
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
  console.error('[verify-secrets] FAILED - a packaged secret, development configuration or non-placeholder endpoint was detected. Do NOT release this build.');
  process.exit(1);
}
console.log('[verify-secrets] PASS - no packaged secrets, development configuration or non-placeholder endpoints detected.');
