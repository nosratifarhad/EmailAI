# 17 - Conversations and threads

**Purpose.** Let the user see at a glance which messages belong to a conversation, read that conversation
without leaving the message list, and use the AI on the whole conversation - decided from Exchange's own
conversation identity, never from a subject.

**Scope.** The conversation model, the conversation state the list and the reading pane read, the batch
lookup endpoint, the conversation badge in the message list, the conversation timeline in the reading
pane, and the availability of "Summarize thread". Not in scope: creating or splitting conversations,
moving messages between them, per-conversation read/archive/mute actions, an Outlook-style collapsible
conversation group in the list, and rendering the attachments of the other messages in the timeline.

**Inputs.** The user's mailbox over the existing authenticated EWS connection; the conversation id a
message carries (in a folder page or a message detail); the REST calls the UI makes
(`POST /api/conversations/summary`, `GET /api/messages/{itemId}/thread`).

**Outputs.** A `ConversationSummary` per requested conversation (identity, topic, message count,
participants, `IsThread`), the badge and participants line in the list, the conversation timeline above
the reading pane, and the enabled/disabled state of "Summarize thread". No mailbox state is written.

**Responsibilities.**

| Layer | Owns |
| --- | --- |
| `src/EmailAI.Domain/Mail/Conversation.cs` | The conversation model, the ONE definition of a thread (`IsThread`), participants |
| `src/EmailAI.Domain/Mail/Thread.cs` | `ThreadMessage` (depth, parent, preview) and `MessageThread.Conversation` |
| `src/EmailAI.Infrastructure/Exchange/EwsExchangeMailService.cs` | `GetConversationSummariesAsync` (one batched conversation read), `GetThreadAsync` |
| `src/EmailAI.Infrastructure/Exchange/EwsMapper.cs` | EWS conversation items → `ConversationSummary`; a sender read that survives a missing `From` |
| `src/EmailAI.Api/Endpoints/MailEndpoints.cs`, `Web/EmailApiClient.cs`, `Web/MailContracts.cs` | The REST surface and the typed client |
| `src/EmailAI.Api/Web/ConversationIndex.cs` | Which conversations are known, which rows still need an answer |
| `src/EmailAI.Api/Components/Pages/MailClient.razor` | Badge, timeline, the AI button's state, and when to ask |

**Invariants.**

1. **Identity is Exchange's conversation id**, never a subject or a `RE:`/`FW:` prefix. A reply whose
   subject was edited stays in the same conversation; two unrelated messages that share a subject stay
   two conversations.
2. **One definition of a thread.** `ConversationSummary.IsThread` is `MessageCount > 1` - an original
   plus at least one reply - and it is derived from the conversation, so it is true for *every* message
   of that conversation: the original, a reply, the oldest or the newest, whichever the user opened.
3. **Never a guess.** A conversation Exchange has not answered for is *unknown*
   (`ConversationSummary.NotLoaded`), which is not "standalone": an unknown conversation shows no badge
   and cannot enable "Summarize thread", and it is never turned into a thread by a heuristic.
4. **One state, three surfaces.** The list badge, the reading pane and the availability of
   "Summarize thread" all read the same `ConversationSummary` through `ConversationIndex`; they cannot
   disagree about what a thread is.
5. **The first paint stays one Exchange query** (spec 11): the conversation lookup for the listed rows
   runs only in the interactive circuit, after the list is painted. Opening a message - including a
   notification deep link (`/?item=...`) - additionally reads that message, its conversation state and,
   for a conversation, the timeline: those are exactly the actions the user asked for.
6. **Bounded.** One lookup asks Exchange about at most `ConversationSummary.MaxLookupBatch` (50)
   conversations; at most `ConversationSummary.MaxParticipants` (5) participants are kept; the list asks
   only for the conversations of the page it shows and only for the ones it does not already know.
7. **Per circuit, per mailbox.** Conversation state is remembered for the circuit and forgotten when the
   configured mailbox changes, so the previous mailbox's conversations can never decorate the new one's
   rows.
8. **A conversation can never break the mail.** A failed lookup produces no badge and no timeline; a row
   without a badge is a row the application claims nothing about.
9. **"Summarize thread" is offered only for a conversation with more than one message**, and the click
   handler refuses it again for anything else - a disabled button cannot be bypassed into a thread
   summary of a standalone message.
10. **Summarization always covers the whole conversation.** The existing thread-summarize operation
    loads every message of the conversation server-side and always did; this specification changed only
    *when* the button is offered.
11. **The disabled state is a real boolean.** A Razor attribute that mixes an implicit expression with
    literal text (`disabled="@A || B"` - an implicit expression ends at the first space) renders the
    literal text as the attribute value, and any non-empty `disabled` value disables the control. The
    button's markup is therefore an explicit expression, pinned by a prerendered-HTML test.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Conversation lookup fails (mailbox/connectivity) | No badge, no timeline, the button says it is still checking; list, reading and reply are unaffected |
| Conversation Exchange no longer holds | Omitted from the answer - never reported as a conversation of one |
| Request without ids, or more than 50 | `400 bad_request` before any Exchange call |
| A message with no conversation id | Nothing is asked; no badge, no timeline, the button explains why |
| `From` missing on an item (a draft, some system items) | Read as "no sender" - the listing, the timeline and the participants survive (`EwsMapper` never reads a property the property set did not load) |
| Timeline item without a body preview | That entry renders without a preview line; the rest of the timeline is unaffected |
| A message moved/deleted while a conversation is open | The unreadable message is absent from the timeline; the failure is reported in the conversation block with a retry |

**Security constraints.** Conversation ids are opaque Exchange ids the server already handed to the
client; they are mailbox-scoped, contain no secret and are used only to ask that mailbox's Exchange about
a conversation. The lookup is read-only. The feature adds no credential read, no configuration surface,
no endpoint that returns account data and no change to authentication or fallback behaviour; errors stay
typed and secret-free (technical detail stays in the structured log).

**Configuration ownership.** None: fixed server/UI constants (lookup batch size, participants kept) that
need no user or administrator configuration. Conversations themselves belong to the mailbox.

**Implementation.** `src/EmailAI.Domain/Mail/Conversation.cs`, `Thread.cs`,
`src/EmailAI.Infrastructure/Exchange/EwsExchangeMailService.cs` (`GetConversationSummariesAsync`,
`ConversationItemPropertySet`, `GetThreadAsync`, `ThreadItemPropertySet`), `EwsMapper.cs`
(`ToConversationSummary`, `ToThreadMessage`, `SenderOf`), `src/EmailAI.Api/Endpoints/MailEndpoints.cs`
(`POST /api/conversations/summary`), `src/EmailAI.Api/Web/MailContracts.cs`, `ConversationIndex.cs`,
`EmailApiClient.cs`, `src/EmailAI.Api/Components/Pages/MailClient.razor`,
`src/EmailAI.Api/wwwroot/app.css`, `src/EmailAI.Application/Exchange/IExchangeMailService.cs`
(`GetConversationSummariesAsync`).

**Tests.** `ConversationSummaryTests` (the definition of a thread, a conversation of one, a subject that
changed, the same subject in two conversations, participant de-duplication, order, bound and blank
senders, thread projection), `ConversationIndexTests` (known vs unknown, which rows still need an answer,
duplicates, bound, reset), `ConversationEndpointsTests` (several conversations in one Exchange call, an
omitted conversation, `400` validation, the typed mailbox failure without internals, the typed client
request), `ThreadedMailUiTests` (prerendered HTML: the first paint makes **no** conversation lookup and
still renders the chevron expand control; a threaded message is badged and offers "Summarize thread"; a
standalone message is disabled with the reason in its tooltip; a message outside any conversation is
never asked about; an unreadable conversation is not guessed to be a thread).

*Coverage boundary.* The interactive click behaviour is pinned through the state objects the component
delegates to (`ConversationIndex`, `MailFolderSidebar`) plus the prerendered markup and the client
request; the repository deliberately references no Blazor component-test framework, so a browser-level
click test is a manual check (14). The batched EWS conversation read is Exchange's documented behaviour
and is pinned at the service boundary by the host/fake tests and by a live probe against a real mailbox
(one `GetConversationItems` per page of conversations).
