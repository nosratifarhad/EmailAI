# 04 - Current-user identity

**Purpose.** Tell the AI **who it is helping** (`MailboxIdentity`) from a trusted source, so no
identity is ever inferred from email content.

**Scope.** Candidate selection, directory resolution, provenance, caching and the API surface of
the identity. Prompt placement is specified in 07.

**Inputs.** The effective `ExchangeOptions` (impersonated mailbox, configured account, endpoint,
mode), the Windows process identity, the Exchange directory (`ResolveNames`).

**Outputs.** A `MailboxIdentity` (display name, SMTP address, account name, `Source`) that is
handed to every AI operation, plus a non-secret `currentUser` object in
`GET /api/settings/exchange`.

**Responsibilities.** `MailboxIdentity` (normalisation, aliases, `Describe`),
`IMailboxIdentityProvider`, `EwsMailboxIdentityProvider` (candidates + resolution + cache),
`EwsExchangeMailService`/`ExchangeAuthRunner` (the authenticated EWS call),
`SettingsContractMapping` (hiding deployment-managed values).

**Invariants.**

1. Resolution priority: impersonated mailbox → configured account → Windows/process account.
2. Each candidate is tried through the Exchange **directory**; the first that resolves wins.
3. A Windows/process account is tried both as `DOMAIN\user` **and** as the bare account name,
   because a live Exchange directory returns NO RESULTS for the qualified form. Only one
   authenticated EWS session is used for all candidates.
4. The result never throws: an unreachable/not-configured Exchange degrades to the locally known
   account (`ConfiguredMailbox`/`ConfiguredAccount`/`WindowsIdentity`) or `Unknown`.
5. The cache key is the configuration fingerprint (endpoint/mode/account/mailbox + local
   account) and **never** the password; a settings change is observable without a restart.
6. The identity carries no secret and cannot be edited by a user.
7. An unknown identity stays unknown (the prompt says so and forbids guessing a name/signature).
8. Email content never participates in resolution.

**Failure modes.**

| Failure | Behaviour |
| --- | --- |
| Exchange not configured / placeholder endpoint | No directory query; local identity (or `Unknown`) is used and cached briefly |
| Directory returns nothing for every candidate | Local identity is used and flagged with its weaker `Source` |
| Directory call fails (auth/timeout/connectivity) | Degraded identity, debug log with the classified kind only - mail and AI keep working |
| Configuration unreadable (credential store) | Falls back to the local account; never throws |

**Security constraints.** Only non-secret values are involved (display name, SMTP address,
account name). A value supplied by the **deployment** configuration (a service account the user
never typed) is suppressed in the API response, exactly like the account fields; the AI still
receives the full identity server-side.

**Configuration ownership.** Implicit - derived from the configured Exchange account. The
Settings page shows it read-only ("Mailbox used by the assistant").

**Implementation.** `src/EmailAI.Domain/Exchange/MailboxIdentity.cs`,
`src/EmailAI.Application/Exchange/IMailboxIdentityProvider.cs`,
`src/EmailAI.Infrastructure/Exchange/EwsMailboxIdentityProvider.cs`
(`ResolveCandidates` is `internal static` for direct testing),
`src/EmailAI.Api/Endpoints/SettingsEndpoints.cs`.

**Tests.** `MailboxIdentityTests` (normalisation, aliases, `Describe`),
`MailboxIdentityCandidatesTests` (candidate order, qualified + bare forms, duplicates, unknown
identity), `AiPromptsTests` (the identity reaches the prompt with its aliases),
`ExchangeSettingsEndpointsTests` (the API never leaks a deployment account).
