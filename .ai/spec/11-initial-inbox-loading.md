# 11 - Initial Inbox loading

**Purpose.** The first screen a user sees must already contain their Inbox - never an empty or
loading placeholder that only fills after an interaction.

**Scope.** Cold start of the UI (Electron or browser), the prerender → interactive hand-off, and
the resilience behaviour for a hiccup. Folder switching and paging are ordinary list loads.

**Inputs.** The default folder (`inbox`), the folder page returned by
`GET /api/folders/{folder}/messages`, the Blazor render mode, and the circuit's request URL
(which may carry a deep link).

**Outputs.** Server-rendered HTML that already contains the message rows (and the folder list),
handed to the interactive circuit through `PersistentComponentState`, so exactly one Exchange
query is made and the interactive first render is not empty.

**Responsibilities.** `MailClient.razor` (`OnInitializedAsync` initialization-pipeline load,
`RegisterOnPersisting`/`TryRestoreInitialPage`, `LoadMessagesAsync`, `ReadDeepLinkItemId`),
`EmailApiClient` (the same-origin call).

**Invariants.**

1. The initial load runs in the **initialization pipeline** (prerendering *and* the interactive
   circuit), not in `OnAfterRenderAsync`, so the very first HTML carries the list.
2. The page fetched while prerendering is persisted and restored by the interactive instance:
   one Exchange query per cold start, and no flash of an empty list.
3. No artificial delay, no forced folder switch and no reload is ever required for the Inbox to
   appear - the app does not depend on timing.
4. An empty folder shows the explicit empty state (never an error, never a blank pane).
5. A **transient** connection failure (the first EWS call of a session can be slow or refused)
   gets exactly **one bounded retry** before the list reports the error - no delay, no credential
   change, no folder switch. A second failure reaches the user with the same message as before.
6. Only genuinely transient failures are retried: connectivity and timeout codes (or an
   unidentified 502/503/504). Authentication, configuration, not-found and mailbox errors are
   answers, not hiccups.
7. A deep link (`/?item=<id>`) is honoured on the initial render; an unusable value is ignored.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| One transient connection failure | Retried once; the first paint still carries the Inbox (two attempts total) |
| Two consecutive failures | The real, user-facing error is shown with the list cleared and a retry affordance |
| Exchange not configured | Typed "not configured" message + Settings path (no crash) |
| Empty mailbox | Explicit empty state |
| Slow backend (shell start) | The shell shows a splash until `/health` answers, then loads the UI once |

**Security constraints.** The initial load carries no secret; the deep-link item id is taken from
the URL and used only as an Exchange item id (an invalid value degrades to "not found"), so it
cannot be used to reach anything else.

**Configuration ownership.** The default folder is a UI constant (`inbox`); the page size is a
UI constant (25).

**Implementation.** `src/EmailAI.Api/Components/Pages/MailClient.razor`,
`src/EmailAI.Api/Web/EmailApiClient.cs`, `src/EmailAI.Api/Program.cs` (Blazor registration).

**Tests.** `InitialInboxLoadTests` - the root page is prerendered **with the inbox messages
already present**, queries only the inbox exactly once, shows the empty state rather than an
error for an empty mailbox, absorbs one transient connection failure with a single retry (two
attempts, Inbox still painted) and surfaces the real error after two failures.
