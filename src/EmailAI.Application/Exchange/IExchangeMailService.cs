using EmailAI.Domain.Exchange;
using EmailAI.Domain.Mail;

namespace EmailAI.Application.Exchange;

/// <summary>
/// The only entry point the rest of the application may use to talk to Exchange.
/// Implementations encapsulate the transport (EWS/SOAP) entirely; no EWS types
/// leak through this boundary.
/// </summary>
public interface IExchangeMailService
{
    /// <summary>
    /// Returns one server-paged page of message headers for a well-known folder
    /// ("inbox", "sent", "drafts", "deleted", "junk", "archive"). Bodies are not loaded.
    /// </summary>
    Task<MessagePage> GetMessagesAsync(
        string folderKey,
        int offset,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Loads a single full message (bodies + recipient lists + attachments metadata).</summary>
    Task<EmailMessage> GetMessageAsync(string itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the conversation a message belongs to, using Exchange conversation
    /// metadata (ConversationId), not subject matching.
    /// </summary>
    Task<MessageThread> GetThreadAsync(string itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a Reply (or Reply All) referencing the original Exchange ItemId,
    /// then verifies a copy was saved in Sent Items.
    /// </summary>
    Task<ReplyResult> ReplyAsync(string itemId, ReplyDraft draft, CancellationToken cancellationToken);

    /// <summary>Probes EWS connectivity without loading the mailbox.</summary>
    Task<ExchangeHealthStatus> CheckHealthAsync(CancellationToken cancellationToken);
}
