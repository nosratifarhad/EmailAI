# 10 - Settings and state synchronization

**Purpose.** A saved configuration must be visible **immediately** everywhere it matters: the
header status pills and the mail view - without a reload, a navigation, a folder switch or a
restart.

**Scope.** The in-process status fan-out, who publishes and who subscribes, and the ordering
guarantees around a save. The HTTP shape of the settings endpoints is 02/05.

**Inputs.** A successful `PUT`/`DELETE` on `/api/settings/ai` or `/api/settings/exchange` (from
the Settings page), the header's periodic health poll (20 s safety net), the mail page's AI
readiness check.

**Outputs.** Every live UI circuit re-evaluates its status the moment a change is published;
an Exchange change additionally re-lists the current folder and clears stale detail.

**Responsibilities.** `AppStatusNotifier` (singleton fan-out + `Revision` counter),
`Settings.razor` (publishes after a successful save/remove), `HealthStatusBadge.razor` and
`MailClient.razor` (subscribe, marshal to their circuit, unsubscribe on disposal).

**Invariants.**

1. Publishing happens only after the save succeeded, so a failed save never claims a new state.
2. The notifier is isolation-safe: a throwing subscriber cannot break the writer or the other
   subscribers (it is logged and skipped).
3. Handlers run on the writer's thread; components marshal to their own dispatcher
   (`InvokeAsync`) and ignore events after disposal.
4. Subscriptions are removed on disposal, so a torn-down circuit does not leak.
5. Observable status does not depend on the next poll; the poll remains a safety net only.
6. An Exchange change (endpoint/mode/account/mailbox) invalidates what is on screen: the folder
   list is re-read and any open message/detail is dropped rather than left stale.
7. The notifier carries an area (`Ai`, `Exchange`, `All`) and a secret-free source label.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Subscriber throws (circuit tearing down) | Logged as a warning; other subscribers still update |
| A component is disposed mid-notification | The event is ignored (`_disposed` guard) |
| Backend restarted | Fresh circuits subscribe on initialization; the poll catches up |
| Save rejected (400/409) | No notification is published; the Settings page shows the validation message |

**Security constraints.** The change event carries no configuration values and no secrets - only
the affected area and a short source label, so a subscriber cannot leak a key by logging a
notification.

**Configuration ownership.** The Settings page is the only writer of per-user configuration
(see 02, 13); the notifier itself holds no configuration.

**Implementation.** `src/EmailAI.Api/Web/AppStatusNotifier.cs`,
`src/EmailAI.Api/Components/Pages/Settings.razor`,
`src/EmailAI.Api/Components/Shared/HealthStatusBadge.razor`,
`src/EmailAI.Api/Components/Pages/MailClient.razor`.

**Tests.** `AppStatusNotifierTests` (fan-out to every subscriber, area forwarding, revision
counter, unsubscribe, a throwing subscriber never breaking the writer or the others),
`AiReadinessEndpointTests` (the status a subscriber re-reads), `ExchangeSettingsEndpointsTests`
(the save that triggers the publish).
