# 09 - Notifications

**Purpose.** Tell a desktop user that new mail arrived, without duplicating toasts and without
putting mail content where it does not belong.

**Scope.** The new-mail feed, baseline/detection/de-duplication, the notification payload, the
toast and the deep link. Folder navigation and the mail list are 11 / 01.

**Inputs.** The inbox header page from Exchange (cheap query - no bodies), the shell's poll
timer, the window state.

**Outputs.** `GET /api/notifications/mail?folder=inbox` →
`{ folder, baseline, count, items[{ id, fromName, fromAddress, subject, receivedAt }] }`, and one
native Windows toast per newly arrived item (sender as title, subject as body).

**Responsibilities.** `MailNotificationService` (in-memory detection + de-duplication),
`NotificationEndpoints` (HTTP contract), `desktop/main.js` (polling, toasts, click → deep link),
`MailClient.razor` (opening `/?item=<id>`).

**Invariants.**

1. The **first poll of a session is a baseline**: it records the current mailbox state, reports
   `baseline: true` with no items, and therefore never notifies for mail that already existed.
2. De-duplication and "is it new" are decided **server-side** by item id plus a per-folder
   high-water mark, so re-rendering, re-polling, page reloads and folder switches cannot produce
   duplicates or resurrect old mail.
3. Only items newer than the high-water mark (or without a timestamp) are reported, oldest first.
4. The payload is header data only: **no body, no credentials, no tokens, no API keys**.
5. The state is per process and in memory: restarting the backend re-establishes a silent
   baseline. Remembered ids are bounded (1000 per folder, FIFO).
6. The toast is raised by the shell, at most five per poll; a failing poll never affects the mail
   UI (errors are logged once per distinct message).
7. A notification click focuses the window and opens exactly that message; an unusable id is
   ignored rather than breaking the mail page.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Exchange unreachable when polling | The endpoint answers a typed error (503/504); the shell logs it once and keeps polling; the mail UI is unaffected |
| First poll fails | No baseline is established; the next successful poll becomes the baseline - still no false notifications |
| Non-Windows host / no OS notification support | Notifications are disabled (logged) |
| `EMAILAI_DISABLE_NOTIFICATIONS=1` | The shell never polls for notifications |
| Window closed while a toast is clicked | The click is ignored (no window to focus) |
| Item moved/deleted before the click | The mail page reports the normal "not found" state |

**Security constraints.** Toast content is intentionally minimal (sender + subject): a toast can
appear on a lock screen, so no message body, no credentials, no API key and no HTML ever reaches
it. The feed endpoint exposes nothing that the mail list does not already show.

**Configuration ownership.** Poll interval and folder are shell constants (`inbox`, 30 s); the
disable switch is an environment variable for the desktop process. Server/browser deployments
never call the endpoint.

**Implementation.** `src/EmailAI.Application/Notifications/IMailNotificationService.cs`,
`MailNotificationService.cs`, `src/EmailAI.Domain/Mail/MailNotification.cs`,
`src/EmailAI.Api/Endpoints/NotificationEndpoints.cs`, `desktop/main.js`.

**Tests.** `MailNotificationServiceTests` (baseline, only new mail, no duplicates on re-poll,
per-folder baselines and folder switching, oldest-first ordering, timestamp-less arrivals,
header-only shape), `NotificationEndpointsTests` (JSON envelope through the real host, folder
handling, one header query per poll, no secret-ish fields).
