# 16 - Mail folders and custom folder navigation

**Purpose.** Let the user see and read the mailbox hierarchy their Exchange account actually holds -
the custom folders they created, nested under Inbox, exactly like Outlook.

**Scope.** The folder model, folder discovery (direct children of a folder), the REST folder key of a
user-created folder, message retrieval from any folder, and the sidebar state that renders the
hierarchy. Not in scope: creating/renaming/deleting/moving folders (the application is a reader),
mailbox-wide folder trees beyond one level of discovery, per-folder rules, unread counts, and a
browser-level component test harness (see *Tests*).

**Inputs.** The user's Exchange mailbox over the existing authenticated EWS connection, the folder key
of the parent the user expanded, and the REST calls the UI makes
(`GET /api/folders/{folderKey}/children`, `GET /api/folders/{folderKey}/messages`).

**Outputs.** A `MailFolder` list per requested parent (identity, display name, parent, well-known
type, whether anything is nested below it), a rendered sidebar whose rows are the fixed top-level
folders plus the discovered children indented one level under Inbox, and message pages for whichever
folder is selected. No new mailbox state is written anywhere.

**Responsibilities.**

| Layer | Owns |
| --- | --- |
| `src/EmailAI.Domain/Mail/MailFolder.cs` | The folder model and its well-known type names |
| `src/EmailAI.Infrastructure/Exchange/CustomFolderKey.cs` | The REST key of a user-created folder (encode/decode/validate) |
| `src/EmailAI.Infrastructure/Exchange/EwsExchangeMailService.cs` | Folder discovery and per-folder message retrieval over EWS |
| `src/EmailAI.Infrastructure/Exchange/EwsMapper.cs` | EWS folder → `MailFolder` (identity from the folder id) |
| `src/EmailAI.Api/Endpoints/MailEndpoints.cs`, `Web/EmailApiClient.cs` | The REST surface and the typed client |
| `src/EmailAI.Api/Web/MailFolderSidebar.cs` | Which rows are visible, in which order, at which indent, expanded and selected |
| `src/EmailAI.Api/Components/Pages/MailClient.razor` | Rendering the rows and the asynchronous load behind the expand action |

**Invariants.**

1. **Folders come from Exchange.** No folder name is hard-coded, no sample folder exists, and the UI
   renders only what `GET /api/folders/{folderKey}/children` returned. The fixed top-level list
   (`inbox`, `sent`, `drafts`, `deleted`, `junk`, `archive`) is the pre-existing well-known set.
2. **Identity is the Exchange folder id, never the display name.** A custom key is
   `folder:` + the hex of the UTF-8 folder id: URL-safe (no `/`, no `+`, no `%`), opaque, and stable
   across a rename. Names are neither unique nor stable, so they never address a folder and never
   decide the selection.
3. **The key cannot collapse two folders.** The alphabet is uppercase-only hex, so the app's
   case-insensitive folder-keyed caches (the new-mail feed) can never map two different folder ids to
   the same entry.
4. **Only direct children.** Discovery runs one shallow folder listing per page, so a folder deeper in
   the mailbox is a child of *its* parent and is never reported under Inbox. A calendar, contacts,
   tasks or search folder is never reported as a mail folder child.
5. **Lazy, bounded discovery.** The first paint performs exactly one Exchange query - the Inbox
   message page (spec 11) - and no folder walk. The walk runs on the first expand, is bounded (100
   folders per page, at most 5 pages, at most 500 folders) and its result is cached for the lifetime
   of the circuit. Re-expanding, collapsing and switching folders never repeat it.
6. **The arrow and the label are different controls.** Expanding/collapsing never changes the selected
   folder; selecting a folder never changes what is expanded - except that selecting a discovered
   child keeps its parent open, so the selection stays visible. The arrow is offered while discovery
   is unknown, and disappears once a successful empty answer proves there is nothing below.
7. **Selecting a folder loads only that folder.** A custom folder is another source for the existing
   message list: same paging, sorting, empty state, refresh, selection and error handling. The list is
   cleared and marked loading before the request, and a response superseded by a newer selection is
   discarded, so stale messages from the previous folder are never shown.
8. **Discovery failure is contained.** A failed folder walk is reported inside the folder pane (with a
   retry) and every top-level folder keeps working. Inbox/Sent/Deleted never depend on it.
9. **Changing the mailbox invalidates the hierarchy.** An Exchange settings change resets the
   discovered folders, ignores an in-flight load, and returns the selection to Inbox if a custom
   folder was selected (that folder may not exist in the new mailbox).
10. **Nothing secret is involved.** A folder key is an opaque Exchange folder id, not a credential. No
    EWS password, account or raw fault is returned to the UI or written to a log by this feature.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Exchange unavailable / folder walk fails | `503 exchange_connection_failed`; the pane shows the message + retry; all top-level folders keep working |
| Folder walk fails while the page opens | Not attempted on the first paint (invariant 5); Inbox renders as usual |
| Selected folder deleted/moved in Outlook | `404 folder_not_found` when Exchange reports the folder as missing (`ErrorFolderNotFound`); any other Exchange answer for an unusable folder id stays a typed `502`/`503`. Either way the message list shows its normal error state with a retry, never a raw fault |
| No access to the selected folder | `502 exchange_authentication_failed` (the existing access-denied classification); no raw EWS fault |
| Malformed / unknown folder key | `400 bad_request`, before any Exchange call |
| Folder has no children | The arrow disappears after the first successful (empty) discovery; Inbox stays selected |
| Folder has no display name | Rendered as `(unnamed folder)`, never a blank row |
| Very large folder list | Bounded by the discovery limits; the walk stops instead of scanning the whole mailbox |
| Discovery in flight while the mailbox changes | Superseded by the settings-change reset (invariant 9) |

**Security constraints.** The folder key travels to the UI and back but contains no secret and no
display data; it is only ever used to address that folder over the same authenticated Exchange
connection. The feature adds no credential read, no endpoint that returns account data, no change to
authentication mode or fallback behaviour, and no configuration surface of its own. Errors are typed
and secret-free (the technical detail stays in the structured log).

**Configuration ownership.** None: the folder walk has fixed UI/server constants (page size, page
count, maximum folders) and needs no user or administrator configuration. The folders themselves are
owned by the user's mailbox.

**Implementation.** `src/EmailAI.Domain/Mail/MailFolder.cs`,
`src/EmailAI.Infrastructure/Exchange/CustomFolderKey.cs`, `FolderMapping.cs`, `EwsMapper.cs`
(`ToChildFolder`), `EwsExchangeMailService.cs` (`GetChildFoldersAsync`, `FindChildFoldersAsync`,
`FindItemsInFolderAsync`, `ResolveCustomFolderIdAsync`, `CollectChildFolders`, `IsMailFolder`),
`src/EmailAI.Api/Endpoints/MailEndpoints.cs` (`GET /api/folders/{folderKey}/children`),
`src/EmailAI.Api/Web/EmailApiClient.cs`, `FolderCatalog.cs`, `MailFolderSidebar.cs`,
`src/EmailAI.Api/Components/Pages/MailClient.razor`, `src/EmailAI.Api/wwwroot/app.css`,
`src/EmailAI.Api/Middleware/ExceptionHandlingMiddleware.cs` (`404 folder_not_found`),
`src/EmailAI.Application/Exchange/IExchangeMailService.cs` (`GetChildFoldersAsync`).

**Tests.** `CustomFolderKeyTests` (identity, round-trip, URL-safety, malformed keys, no case-collapse,
well-known/custom separation), `MailFolderEndpointsTests` (the children contract through the real host,
direct-children-only, a custom key listing its own messages, an empty folder, and the typed
`400`/`404`/`502`/`503` failures without internals), `MailFolderSidebarTests` (expand, collapse,
selection preservation, child selection keeps the parent open, indentation depth, same-named folders,
empty folder, failed discovery with retry, reset), `CustomMailFolderUiTests` (first paint renders the
hierarchy control with **no** folder walk, top-level order and the single arrow, Inbox still rendered
when the walk would fail, and the typed client's encoded children request).

*Coverage boundary.* The interactive click behaviour is tested through the state object the component
delegates to (`MailFolderSidebar`), plus the prerendered markup and the client request; the repository
deliberately references no Blazor component-test framework, so a browser-level click test is a manual
check (14). The EWS traversal itself (a shallow listing returns only direct children) is Exchange's
documented behaviour and is pinned at the service boundary by the host/fake tests rather than
simulated. The folder-gone classification is pinned by the typed host test (`ErrorFolderNotFound` →
`404 folder_not_found`); it cannot be provoked against a live mailbox without deleting a folder the
user owns - a live probe with an id that never existed answered a typed `502 exchange_mailbox_error`,
which is the same contained behaviour (no raw fault, list error state with a retry).
