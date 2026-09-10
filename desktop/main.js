'use strict';

// EmailAI desktop shell.
//
// Electron is ONLY a shell. The actual application is the bundled ASP.NET Core
// Blazor app (EmailAI.Api.exe, self-contained win-x64). This main process:
//   1. picks a free localhost port,
//   2. starts the bundled backend on 127.0.0.1:<port>,
//   3. polls GET /health until the backend is ready,
//   4. loads the Blazor UI from http://127.0.0.1:<port>/ in a BrowserWindow,
//   5. polls the backend's new-mail feed and raises a native Windows notification per new
//      message (clicking one focuses the window and opens that message),
//   6. stops the backend when the app quits (no orphan process).
//
// The renderer never sees AI provider keys, Exchange passwords or any other secret:
// all configuration stays server-side in the backend process.

const { app, BrowserWindow, dialog, shell, Notification } = require('electron');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const http = require('node:http');
const net = require('node:net');
const path = require('node:path');

const SERVER_EXE = 'EmailAI.Api.exe';
const HEALTH_TIMEOUT_MS = 60 * 1000; // first JIT + Defender scan can be slow
const HEALTH_INTERVAL_MS = 250;
const MAX_START_ATTEMPTS = 4;

// New-mail notifications: the backend detects and de-duplicates new mail (see
// IMailNotificationService), this process only renders what it reports. The first poll of a
// session establishes the baseline, so starting the app never notifies for existing mail.
const NOTIFICATION_POLL_MS = 30 * 1000;
const NOTIFICATION_FOLDER = 'inbox';
const MAX_NOTIFICATIONS_PER_POLL = 5;

let mainWindow = null;
let serverProcess = null;
let serverPort = null;
let stopping = false;
let logWriteStream = null;
let notificationTimer = null;
let notificationPolling = false;
let lastNotificationError = null;

// ------------------------------------------------------------------ logging

function log(message) {
  const line = `[${new Date().toISOString()}] ${message}\n`;
  if (logWriteStream) {
    try { logWriteStream.write(line); } catch { console.log(line.trim()); }
  } else {
    console.log(line.trim());
  }
}

function logPath(fileName) {
  return path.join(app.getPath('userData'), fileName);
}

function openLogStream(fileName) {
  try {
    const stream = fs.createWriteStream(logPath(fileName), { flags: 'a' });
    stream.on('error', () => { /* ignore stream errors */ });
    return stream;
  } catch {
    return null;
  }
}

function stateFile() {
  return logPath('server-state.json');
}

// Written so external tooling (and tests) can observe the dynamic port/PID.
function writeState(status) {
  try {
    fs.writeFileSync(stateFile(), JSON.stringify({
      status,
      pid: serverProcess ? serverProcess.pid : null,
      port: serverPort,
      updatedAt: new Date().toISOString(),
    }, null, 2));
  } catch (error) {
    log('writeState failed: ' + error.message);
  }
}

// ------------------------------------------------------ backend discovery

function getServerExePath() {
  const candidates = app.isPackaged
    ? [
        path.join(process.resourcesPath, 'server', SERVER_EXE),
        path.join(process.resourcesPath, 'server', 'aspnet-publish', SERVER_EXE),
      ]
    : [path.join(__dirname, 'aspnet-publish', SERVER_EXE)];
  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return candidate;
  }
  return candidates[0];
}

function pickFreePort() {
  return new Promise((resolve, reject) => {
    const probe = net.createServer();
    probe.unref();
    probe.on('error', reject);
    probe.listen(0, '127.0.0.1', () => {
      const port = probe.address().port;
      probe.close(() => resolve(port));
    });
  });
}

function httpGet(port, requestPath) {
  return new Promise((resolve) => {
    const req = http.get(
      { host: '127.0.0.1', port, path: requestPath, timeout: 2000 },
      (res) => {
        res.resume();
        resolve({ statusCode: res.statusCode });
      },
    );
    req.on('timeout', () => { req.destroy(); resolve(null); });
    req.on('error', () => resolve(null));
  });
}

// Same as httpGet, but reads and parses a JSON body (used by the new-mail feed).
function httpGetJson(port, requestPath) {
  return new Promise((resolve) => {
    const req = http.get(
      { host: '127.0.0.1', port, path: requestPath, timeout: 5000 },
      (res) => {
        if (res.statusCode !== 200) {
          res.resume();
          resolve(null);
          return;
        }

        let body = '';
        res.setEncoding('utf8');
        res.on('data', (chunk) => { body += chunk; });
        res.on('end', () => {
          try {
            resolve(JSON.parse(body));
          } catch {
            resolve(null);
          }
        });
      },
    );
    req.on('timeout', () => { req.destroy(); resolve(null); });
    req.on('error', () => resolve(null));
  });
}

function waitForHealth(port, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve) => {
    const poll = async () => {
      if (Date.now() >= deadline) {
        resolve(false);
        return;
      }
      const result = await httpGet(port, '/health');
      if (result && result.statusCode === 200) {
        resolve(true);
        return;
      }
      setTimeout(poll, HEALTH_INTERVAL_MS);
    };
    poll();
  });
}

// ------------------------------------------------------- backend process

function startServer(port) {
  return new Promise((resolve, reject) => {
    const exePath = getServerExePath();
    if (!fs.existsSync(exePath)) {
      reject(new Error('Bundled backend not found at ' + exePath));
      return;
    }

    const env = {
      ...process.env,
      // Our dynamic port wins over any baked-in URL configuration.
      ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
      ASPNETCORE_ENVIRONMENT: process.env.ASPNETCORE_ENVIRONMENT || 'Production',
    };

    // spawn() requires integer file descriptors (or 'pipe'/'ignore'/'inherit')
    // in stdio - NOT async fs.WriteStream objects.
    const outFd = fs.openSync(logPath('server.out.log'), 'a');
    const errFd = fs.openSync(logPath('server.err.log'), 'a');

    log(`Starting backend: ${exePath} on port ${port}`);
    let child;
    try {
      child = spawn(exePath, ['--urls', `http://127.0.0.1:${port}`], {
        cwd: path.dirname(exePath), // content root = publish folder (appsettings/wwwroot)
        env,
        windowsHide: true,
        stdio: ['ignore', outFd, errFd],
      });
    } catch (error) {
      try { fs.closeSync(outFd); } catch { /* ignore */ }
      try { fs.closeSync(errFd); } catch { /* ignore */ }
      log('Failed to spawn backend: ' + error.message);
      reject(error);
      return;
    }

    // Child process inherited duplicates of the handles; close our copies.
    fs.closeSync(outFd);
    fs.closeSync(errFd);

    child.on('error', (error) => {
      log('Failed to spawn backend: ' + error.message);
      reject(error);
    });

    child.on('exit', (code, signal) => {
      if (!stopping && serverProcess === child) {
        log(`Backend process exited unexpectedly (code=${code}, signal=${signal})`);
        serverProcess = null;
        serverPort = null;
        writeState('exited');
      }
    });

    resolve(child);
  });
}

function stopServer() {
  const child = serverProcess;
  serverProcess = null;
  serverPort = null;
  writeState('stopped');
  if (child && child.exitCode === null && !child.killed) {
    log(`Stopping backend (pid=${child.pid})`);
    try {
      child.kill(); // Windows: TerminateProcess; synchronous enough to avoid orphans
    } catch (error) {
      log('kill failed: ' + error.message);
    }
  }
}

async function ensureServerReady() {
  for (let attempt = 1; attempt <= MAX_START_ATTEMPTS; attempt += 1) {
    log(`Start attempt ${attempt}/${MAX_START_ATTEMPTS}`);
    const port = await pickFreePort();
    try {
      serverProcess = await startServer(port);
      serverPort = port;
      writeState('starting');
      const startedAt = Date.now();
      const ready = await waitForHealth(port, HEALTH_TIMEOUT_MS);
      if (ready) {
        log(`Backend healthy after ${Date.now() - startedAt} ms on port ${port}`);
        writeState('running');
        return port;
      }
      log(`Backend not healthy on port ${port}; retrying with a new port.`);
      stopServer();
    } catch (error) {
      log('Start attempt failed: ' + error.message);
      serverProcess = null;
      serverPort = null;
    }
  }
  throw new Error(
    'EmailAI backend failed to start after several attempts.\n' +
    'Diagnostics: ' + app.getPath('userData'),
  );
}

// ------------------------------------------------------------------ window

function showLoadingWindow() {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 900,
    show: false,
    autoHideMenuBar: true,
    title: 'EmailAI',
    backgroundColor: '#f3f4f6',
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  mainWindow.once('ready-to-show', () => mainWindow.show());
  mainWindow.on('closed', () => { mainWindow = null; });

  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) shell.openExternal(url);
    return { action: 'deny' };
  });

  mainWindow.webContents.on('will-navigate', (event, targetUrl) => {
    const appOrigin = serverPort ? `http://127.0.0.1:${serverPort}` : null;
    const isSplash = targetUrl.startsWith('data:text/html');
    const staysInApp = appOrigin && targetUrl.startsWith(appOrigin);
    if (!isSplash && !staysInApp) {
      event.preventDefault();
      if (/^https?:/i.test(targetUrl)) shell.openExternal(targetUrl);
    }
  });

  // Feedback while the bundled backend boots for the first time.
  const splash = 'data:text/html;charset=utf-8,' + encodeURIComponent(
    '<!doctype html><html><head><meta charset="utf-8"><style>' +
    'body{font-family:Segoe UI,system-ui,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#f3f4f6;color:#1f2937;}' +
    'div{text-align:center}h1{font-weight:600;margin:0 0 8px}p{color:#4b5563}' +
    '</style></head><body><div><h1>EmailAI</h1><p>Starting local mail server&hellip;</p></div></body></html>',
  );
  mainWindow.loadURL(splash).catch((error) => {
    // ERR_ABORTED is expected when loadApp() replaces the splash URL.
    if (!error || (error.errno !== -3 && error.code !== 'ERR_ABORTED')) {
      log('Splash load failed: ' + (error ? error.message : String(error)));
    }
  });
  return mainWindow;
}

function loadApp() {
  loadAppUrl('');
}

// Loads the UI relative to the app root ("", "?item=<id>"). Anything other than the app
// origin is still handled by the will-navigate guard in the window.
function loadAppUrl(suffix) {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  const url = `http://127.0.0.1:${serverPort}/${suffix}`;
  log(`Loading UI from ${url}`);
  mainWindow.loadURL(url).catch((error) => {
    log('loadURL failed: ' + error.message);
    dialog.showErrorBox('EmailAI', 'The EmailAI UI could not be loaded.\n\n' + error.message);
  });
}

// ------------------------------------------------------- new-mail notifications

// Native notifications are a Windows-desktop feature of the shell; a headless run can opt
// out entirely with EMAILAI_DISABLE_NOTIFICATIONS=1.
function notificationsEnabled() {
  if (process.env.EMAILAI_DISABLE_NOTIFICATIONS === '1') return false;
  if (process.platform !== 'win32') return false;
  if (typeof Notification !== 'function') return false;
  if (typeof Notification.isSupported === 'function' && !Notification.isSupported()) return false;
  return true;
}

function startMailNotifications() {
  if (notificationTimer || !serverPort) return;
  if (!notificationsEnabled()) {
    log('New-mail notifications are disabled (unsupported platform or EMAILAI_DISABLE_NOTIFICATIONS=1).');
    return;
  }

  log(`New-mail notifications enabled (folder=${NOTIFICATION_FOLDER}, every ${NOTIFICATION_POLL_MS / 1000}s).`);
  // The first poll only establishes the baseline on the server, so this never notifies for
  // mail that was already in the mailbox when the app started.
  void pollMailNotifications();
  notificationTimer = setInterval(() => { void pollMailNotifications(); }, NOTIFICATION_POLL_MS);
  // Never keep the process alive for a timer (the app quits with its window).
  if (typeof notificationTimer.unref === 'function') notificationTimer.unref();
}

function stopMailNotifications() {
  if (notificationTimer) {
    clearInterval(notificationTimer);
    notificationTimer = null;
  }
}

async function pollMailNotifications() {
  if (stopping || !serverPort || notificationPolling || !mainWindow) return;
  notificationPolling = true;
  try {
    const payload = await httpGetJson(
      serverPort,
      `/api/notifications/mail?folder=${encodeURIComponent(NOTIFICATION_FOLDER)}`,
    );

    // Baseline (first poll of the session) and "nothing new" both mean: stay quiet.
    if (!payload || payload.baseline === true) return;

    const items = Array.isArray(payload.items) ? payload.items : [];
    if (items.length === 0) return;

    const shown = items.slice(0, MAX_NOTIFICATIONS_PER_POLL);
    for (const item of shown) showMailNotification(item);
    if (items.length > shown.length) {
      log(`${items.length} new messages detected; showing the first ${shown.length} notifications.`);
    }
    lastNotificationError = null;
  } catch (error) {
    // A notification problem must never affect the mail UI; log it once per distinct error.
    const message = error && error.message ? error.message : String(error);
    if (message !== lastNotificationError) {
      lastNotificationError = message;
      log('New-mail poll failed: ' + message);
    }
  } finally {
    notificationPolling = false;
  }
}

// One native notification per message: sender as the title, subject as the body. Clicking it
// focuses the window and opens that exact message (/?item=<exchange item id>).
function showMailNotification(item) {
  if (!item || typeof item.id !== 'string' || item.id.length === 0) return;

  const title = firstNonEmpty(item.fromName, item.fromAddress) || 'New message';
  const body = firstNonEmpty(item.subject) || '(no subject)';

  try {
    const notification = new Notification({
      title,
      body,
      silent: false,
      timeoutType: 'default',
    });
    notification.on('click', () => openMessageFromNotification(item.id));
    notification.show();
  } catch (error) {
    log('Could not show a mail notification: ' + (error && error.message ? error.message : String(error)));
  }
}

function openMessageFromNotification(itemId) {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
  loadAppUrl(`?item=${encodeURIComponent(itemId)}`);
}

function firstNonEmpty(...values) {
  for (const value of values) {
    if (typeof value === 'string' && value.trim().length > 0) return value.trim();
  }
  return null;
}

// ------------------------------------------------------------ app lifecycle

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });

  app.setAppUserModelId('com.emailai.desktop');

  app.whenReady().then(async () => {
    logWriteStream = openLogStream('main.log');
    log('EmailAI desktop starting (version ' + app.getVersion() + ')');
    showLoadingWindow();

    try {
      const port = await ensureServerReady();
      serverPort = port;
      loadApp();
      startMailNotifications();
      // Optional headless smoke-test hook: quit gracefully after N ms so
      // automated runs can exercise the real quit path (will-quit ->
      // stopServer) and confirm no orphaned EmailAI.Api.exe remains.
      if (process.env.EMAILAI_SMOKE_QUIT_MS) {
        setTimeout(() => app.quit(), parseInt(process.env.EMAILAI_SMOKE_QUIT_MS, 10));
      }
    } catch (error) {
      log('FATAL: ' + error.message);
      if (mainWindow && !mainWindow.isDestroyed()) mainWindow.destroy();
      dialog.showErrorBox('EmailAI', error.message);
      app.exit(1);
    }
  });

  app.on('window-all-closed', () => {
    app.quit();
  });

  app.on('before-quit', () => {
    stopping = true;
  });

  app.on('will-quit', () => {
    stopMailNotifications();
    stopServer();
    if (logWriteStream) {
      try { logWriteStream.end(); } catch { /* ignore */ }
      logWriteStream = null;
    }
  });
}

