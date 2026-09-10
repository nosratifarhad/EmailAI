namespace EmailAI.Domain.Mail;

/// <summary>A single message rendered inside a conversation view.</summary>
public sealed class ThreadMessage : MessageSummary
{
    /// <summary>0 = root of the reply tree; used by the UI for nesting/indentation.</summary>
    public int Depth { get; init; }

    /// <summary>Id of the message this one directly replies to (when known).</summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// Exchange's short body preview (the opening words of the message). It lets a collapsed
    /// conversation entry say what the message is about without loading its body.
    /// </summary>
    public string? Preview { get; init; }
}

/// <summary>
/// A complete conversation, ordered chronologically, built from Exchange
/// conversation metadata (ConversationId) — never by Subject matching alone.
/// </summary>
public sealed class MessageThread
{
    public string? ConversationId { get; init; }
    public string? Topic { get; init; }
    public int TotalCount { get; init; }

    /// <summary>The item that was opened, so the UI can expand the newest relevant message.</summary>
    public string? FocusMessageId { get; init; }

    public IReadOnlyList<ThreadMessage> Messages { get; init; } = [];

    /// <summary>
    /// The people in this conversation: the distinct senders, in the order they appear in
    /// <see cref="Messages"/> (which is chronological), capped for display.
    /// </summary>
    public IReadOnlyList<EmailAddress> Participants => ConversationSummary.SendersOf(Messages);

    /// <summary>
    /// The conversation state this thread represents - the same model the message list badges and
    /// the "Summarize thread" availability read, so the three surfaces can never disagree.
    /// </summary>
    public ConversationSummary Conversation =>
        new(ConversationId, Topic, TotalCount, Participants);
}
