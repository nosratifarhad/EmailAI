using EmailAI.Domain.Exchange;

namespace EmailAI.Application.Exchange;

/// <summary>
/// Resolves the AUTHORITATIVE identity of the mailbox EmailAI is working on - "who is the
/// current user?" - so the AI can be told who it is helping instead of guessing from the
/// email content.
///
/// The implementation prefers the Exchange context (the mailbox configured for impersonation,
/// otherwise the configured account, otherwise the Windows identity), resolves it through the
/// Exchange directory and only then falls back to the locally known values. It must never
/// throw for an environment problem: an unresolved identity is reported as
/// <see cref="MailboxIdentity.None"/> so mail/AI operations keep working.
/// </summary>
public interface IMailboxIdentityProvider
{
    /// <summary>
    /// The current user's mailbox identity. Never throws; never returns a secret.
    /// Implementations may cache, but a configuration change (endpoint/mode/account/mailbox)
    /// must be observable without restarting the application.
    /// </summary>
    Task<MailboxIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
