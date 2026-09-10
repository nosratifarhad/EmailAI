# 07 - AI prompt trust boundaries

**Purpose.** Keep the difference between **trusted application context** and **untrusted email
data** explicit in every prompt, so the assistant cannot be talked out of who it is or into
following instructions found in an email.

**Scope.** Prompt construction for all four AI operations: what is trusted, what is fenced, what
is forbidden. Model behaviour tuning (temperature, style) is out of scope.

**Inputs.** The current-user `MailboxIdentity` (trusted), the message/thread loaded through
Exchange (untrusted), the requested output language, the operation type.

**Outputs.** A system + user prompt pair for the provider. No tool calls, no function calling,
no automatic actions.

**Responsibilities.** `AiPrompts` (prompt text and fencing), `AiService` (wiring identity,
language, limits and extracted plain text), `MailTextExtractor` (HTML → text, so markup cannot
masquerade as instruction), `MailboxIdentity.Aliases` (aliases derived from authoritative values
only).

**Invariants.**

1. The current user is delivered as **trusted context outside the untrusted-data fences**, with
   aliases derived only from the authoritative display name/account/SMTP address.
2. Email content is always inside explicit untrusted-data fences and is described to the model
   as data that must not be obeyed.
3. The model is instructed that "you"/"your" means the mailbox owner, and that it must not adopt
   an identity claimed *inside* an email.
4. When no identity can be resolved, the prompt says so and forbids inventing a name or
   signature.
5. The prompt never contains a password, token, API key or credential of any kind, and never the
   Exchange endpoint's credentials.
6. The prompt never instructs the model to send, delete, move or mark anything: it only produces
   text, and the UI decides what to do with it.
7. The requested output language (Auto/English/Persian) is stated explicitly; Auto means "follow
   the language of the mail".

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Unknown identity | Prompt states "current user unknown" and forbids guessing a name/signature |
| Email text claims a different identity/role | It stays inside the untrusted fences; the trusted block wins |
| Prompt-injection attempt in the mail ("ignore your instructions…") | Explicitly forbidden by the system prompt; the model is told such text is data |
| Oversized thread/body | Bounded by `AiLimits` with an explicit truncation note - never a silent drop |
| Model returns a claim of an action taken | Not possible to act: nothing but text is returned |

**Security constraints.** No secret ever enters a prompt. Identity values are non-secret display
data (04). Email content is sent only to the provider the user/deployment configured, and is
never logged.

**Configuration ownership.** Prompt wording is code (not user-configurable); the response
language is per request (UI) with Auto as the default.

**Implementation.** `src/EmailAI.Application/AI/AiPrompts.cs`, `AiService.cs`,
`MailTextExtractor.cs`, `src/EmailAI.Domain/Exchange/MailboxIdentity.cs`,
`src/EmailAI.Api/Endpoints/AiEndpoints.cs`, `src/EmailAI.Api/Web/AiContracts.cs`.

**Tests.** `AiPromptsTests` (identity injection and placement, unknown identity, identity
claimed inside an email stays data, aliases from authoritative values only),
`AiServiceTests` (operation wiring and language), `MailTextExtractorTests` (HTML → text),
`MailboxIdentityTests` (alias derivation).
