namespace EmailAI.Domain.Mail;

/// <summary>
/// What the application knows about the conversation a message belongs to. This is the ONE concept
/// behind every thread-aware surface: the conversation badge in the message list, the reading pane's
/// conversation view, and whether "Summarize thread" can be used. They therefore cannot disagree
/// about what a thread is.
///
/// Identity is Exchange's own <c>ConversationId</c> - never a subject or a "RE:" prefix - and the
/// message count and participants come from Exchange's conversation metadata. A subject is not an
/// identity: two unrelated mails that happen to share one are two conversations in Exchange, and a
/// reply chain whose subject was edited is still one.
///
/// <see cref="MessageCount"/> is <c>null</c> until the conversation has actually been read from
/// Exchange (<see cref="NotLoaded"/>). An unread conversation is never presented as a thread of one,
/// so the UI never claims "standalone" about a message it has not asked about.
/// </summary>
public sealed record ConversationSummary(
    string? ConversationId,
    string? Topic,
    int? MessageCount,
    IReadOnlyList<EmailAddress> Participants)
{
    /// <summary>How many participants are kept for display (and travel on the wire).</summary>
    public const int MaxParticipants = 5;

    /// <summary>How many conversations one lookup may ask Exchange for.</summary>
    public const int MaxLookupBatch = 50;

    /// <summary>
    /// A conversation whose identity is known but whose content has not been read from Exchange yet.
    /// The count is deliberately unknown: no caller may treat it as a standalone message.
    /// </summary>
    public static ConversationSummary NotLoaded(string? conversationId, string? topic) =>
        new(conversationId, topic, null, []);

    /// <summary>True once Exchange has answered how many messages the conversation holds.</summary>
    public bool IsKnown => MessageCount is not null;

    /// <summary>True when the message addresses a conversation at all (some mailbox items do not).</summary>
    public bool HasConversation => !string.IsNullOrWhiteSpace(ConversationId);

    /// <summary>
    /// The single definition of "this message is part of a thread": Exchange reports more than one
    /// message in its conversation, i.e. an original and at least one reply. It is true for every
    /// message of that conversation - the original, a reply, or the newest one - because it is
    /// derived from the conversation, never from the message the user happened to open.
    /// </summary>
    public bool IsThread => MessageCount is > 1;

    /// <summary>
    /// The distinct senders of the given messages, in the order they first appear (chronological
    /// when the messages are), capped at <see cref="MaxParticipants"/>. Called "participants" because
    /// that is what the UI shows: who this conversation is with.
    /// </summary>
    public static IReadOnlyList<EmailAddress> SendersOf(IEnumerable<MessageSummary> messages)
    {
        var participants = new List<EmailAddress>(MaxParticipants);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var message in messages)
        {
            if (message.From is not { } sender || IsBlank(sender) || !seen.Add(ParticipantKey(sender)))
            {
                continue;
            }

            participants.Add(sender);
            if (participants.Count == MaxParticipants)
            {
                break;
            }
        }

        return participants;
    }

    /// <summary>True for an address Exchange handed back without a name and without an address.</summary>
    private static bool IsBlank(EmailAddress address) =>
        string.IsNullOrWhiteSpace(address.Name) && string.IsNullOrWhiteSpace(address.Address);

    /// <summary>
    /// Identity of one participant: the address when Exchange gave one (display names vary between
    /// messages of the same person), otherwise the name. Shared with the Exchange side so a
    /// conversation's people are de-duplicated by exactly one rule.
    /// </summary>
    public static string ParticipantKey(EmailAddress address) =>
        string.IsNullOrWhiteSpace(address.Address)
            ? $"name:{address.Name?.Trim()}"
            : address.Address.Trim();
}
