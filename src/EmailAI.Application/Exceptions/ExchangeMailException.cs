namespace EmailAI.Application.Exceptions;

/// <summary>
/// Categorised Exchange failures. The API layer maps <see cref="Kind"/> to a
/// user-friendly HTTP response; technical detail is kept in the structured log.
/// </summary>
public enum ExchangeMailErrorKind
{
    Configuration,
    Connectivity,
    Authentication,
    BadRequest,
    NotFound,

    /// <summary>The mailbox folder itself is missing (moved, deleted or never visible to this account).</summary>
    FolderNotFound,
    MailboxError,
    SendFailed,
    Timeout,
}

public sealed class ExchangeMailException : Exception
{
    public ExchangeMailErrorKind Kind { get; }

    public ExchangeMailException(ExchangeMailErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}
