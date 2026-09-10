namespace EmailAI.Domain.Mail;

/// <summary>Lightweight attachment metadata (content is downloaded on demand).</summary>
public sealed record AttachmentInfo(
    string Id,
    string? Name,
    string? ContentType,
    long? Size,
    bool IsInline);

/// <summary>
/// A lightweight message header used in list views. The body is deliberately
/// not part of this model so list queries stay cheap (no full-body EWS loads).
/// </summary>
public class MessageSummary
{
    public required string Id { get; init; }
    public string? Subject { get; init; }
    public EmailAddress? From { get; init; }
    public IReadOnlyList<EmailAddress> To { get; init; } = [];
    public DateTimeOffset? ReceivedAt { get; init; }
    public bool IsRead { get; init; }
    public bool HasAttachments { get; init; }
    public EmailImportance Importance { get; init; } = EmailImportance.Normal;
    public string? ConversationId { get; init; }
    public string? ConversationTopic { get; init; }
}

/// <summary>Full message (loaded when a single message is opened).</summary>
public sealed class EmailMessage : MessageSummary
{
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];
    public IReadOnlyList<EmailAddress> ReplyTo { get; init; } = [];
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];

    /// <summary>Body rendered as HTML (null when the source message is plain text only).</summary>
    public string? BodyHtml { get; init; }

    /// <summary>Plain-text representation of the body (when available from the server).</summary>
    public string? BodyText { get; init; }

    public DateTimeOffset? SentAt { get; init; }
    public string? InternetMessageId { get; init; }

    /// <summary>Message-Id of the message this one replies to, when available.</summary>
    public string? InReplyTo { get; init; }

    /// <summary>The References header chain, when available.</summary>
    public IReadOnlyList<string> References { get; init; } = [];
}
