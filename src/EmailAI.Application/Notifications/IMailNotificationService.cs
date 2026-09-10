using EmailAI.Domain.Mail;

namespace EmailAI.Application.Notifications;

/// <summary>
/// Detects NEWLY arrived received mail so the Windows/Electron shell can raise a native
/// notification (sender + subject). It is a change-detector over the existing
/// <see cref="Exchange.IExchangeMailService"/>, not a new Exchange subscription: EWS offers
/// no push channel here, so a caller polls and this service answers with the difference.
///
/// Guarantees (all covered by tests):
///   * the first poll for a folder only establishes a BASELINE - a fresh application start
///     never notifies for mail that already existed;
///   * the same message is never reported twice, however often the folder is re-listed;
///   * switching folders in the mail UI has no effect on this state (each folder key keeps
///     its own baseline), so no duplicates appear;
///   * only header data (sender/subject/time) is returned - never a body, never a secret.
/// </summary>
public interface IMailNotificationService
{
    /// <summary>
    /// Returns the messages that arrived in <paramref name="folderKey"/> since the previous
    /// poll, oldest first. The very first call for a folder returns
    /// <see cref="MailNotificationPoll.Baseline"/> with no items.
    /// </summary>
    Task<MailNotificationPoll> PollAsync(string folderKey, CancellationToken cancellationToken = default);
}
