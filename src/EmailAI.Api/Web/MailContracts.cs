namespace EmailAI.Api.Web;

/// <summary>
/// Body of <c>POST /api/conversations/summary</c>: the conversations whose state the caller wants to
/// know. The ids are Exchange conversation ids the server already handed out (a message page carries
/// its <c>conversationId</c>); they are opaque, mailbox-scoped and contain no secret. The operation is
/// read-only - it asks Exchange what a conversation holds, and changes nothing.
/// </summary>
public sealed class ConversationLookupRequest
{
    /// <summary>
    /// The conversations to look up. Blank entries are ignored; at most
    /// <see cref="EmailAI.Domain.Mail.ConversationSummary.MaxLookupBatch"/> distinct ids are accepted.
    /// </summary>
    public IReadOnlyList<string> ConversationIds { get; init; } = [];
}
