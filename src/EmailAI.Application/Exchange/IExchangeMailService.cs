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
    /// Returns one server-paged page of message headers for a mailbox folder. The key is either a
    /// well-known folder ("inbox", "sent", "drafts", "deleted", "junk", "archive") or a
    /// custom-folder key returned by <see cref="GetChildFoldersAsync"/> - both address exactly one
    /// Exchange folder. Bodies are never loaded.
    /// </summary>
    Task<MessagePage> GetMessagesAsync(
        string folderKey,
        int offset,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the folders nested directly below <paramref name="parentKey"/> (a well-known key or a
    /// custom-folder key), so the sidebar can offer the mailbox hierarchy Exchange actually holds.
    /// Only direct children are returned and only mail folders: a folder deeper in the tree, or a
    /// calendar/contacts/tasks/search folder, is never reported as a child. The display name is
    /// informational - callers address a folder through <see cref="MailFolder.Id"/>.
    /// </summary>
    Task<IReadOnlyList<MailFolder>> GetChildFoldersAsync(
        string parentKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the conversation state (message count + participants) of the given conversations. The
    /// whole set is read in ONE Exchange round trip, and only what Exchange reports is returned: a
    /// conversation it no longer knows is omitted rather than invented. An empty request makes no
    /// Exchange call at all. Identity is Exchange's conversation id - never a subject or a "RE:"
    /// prefix - so a reply is a thread regardless of how its subject was edited.
    /// </summary>
    Task<IReadOnlyList<ConversationSummary>> GetConversationSummariesAsync(
        IReadOnlyList<string> conversationIds,
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
