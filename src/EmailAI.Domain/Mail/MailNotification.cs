namespace EmailAI.Domain.Mail;

/// <summary>
/// One newly arrived message, in the minimal form a desktop notification needs
/// (sender + subject + when). Bodies are never part of this model: a notification must
/// stay small, must not leak message content into the OS notification centre beyond the
/// header the user already sees in the mail list, and must never carry a secret.
/// </summary>
/// <param name="Id">Exchange item id (used for de-duplication and deep-linking).</param>
/// <param name="FromName">Sender display name, when known.</param>
/// <param name="FromAddress">Sender SMTP address, when known.</param>
/// <param name="Subject">Subject line, when known.</param>
/// <param name="ReceivedAt">Server receive time, when known.</param>
public sealed record MailNotification(
    string Id,
    string? FromName,
    string? FromAddress,
    string? Subject,
    DateTimeOffset? ReceivedAt);

/// <summary>
/// Result of one new-mail poll.
/// </summary>
/// <param name="Baseline">
/// True when this poll only ESTABLISHED the baseline for the folder (the first poll after
/// the backend started). <see cref="Items"/> is always empty in that case so a fresh
/// application start can never notify for mail that already existed.
/// </param>
/// <param name="Items">Messages that arrived since the previous poll, oldest first.</param>
public sealed record MailNotificationPoll(bool Baseline, IReadOnlyList<MailNotification> Items);
