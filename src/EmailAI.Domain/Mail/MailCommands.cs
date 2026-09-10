namespace EmailAI.Domain.Mail;

/// <summary>Server-side paged result used for list views (EWS stays the source of truth).</summary>
public sealed class MessagePage
{
    public IReadOnlyList<MessageSummary> Items { get; init; } = [];
    public int Offset { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>Payload required to send a Reply / Reply All through EWS.</summary>
public sealed class ReplyDraft
{
    public required string Body { get; init; }
    public bool ReplyAll { get; init; }
}

/// <summary>Outcome of the post-send check that a copy landed in Sent Items.</summary>
public sealed class SentVerification
{
    public required bool Found { get; init; }
    public string? ItemId { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? Note { get; init; }
}

/// <summary>Result of a reply operation, including the Sent Items verification.</summary>
public sealed class ReplyResult
{
    public required bool Sent { get; init; }
    public string? SentItemId { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public SentVerification? Verification { get; init; }
}
