namespace EmailAI.Application.AI;

/// <summary>
/// Categorised AI failures. The API middleware maps <see cref="Kind"/> to a
/// user-friendly HTTP response; technical detail is kept in the structured log.
/// Mirrors <c>ExchangeMailException</c> in the Application.Exchange slice.
/// </summary>
public enum AiErrorKind
{
    /// <summary>AI_* environment variables are missing or empty.</summary>
    NotConfigured,

    /// <summary>Present but unusable (e.g. AI_BASE_URL is not an http(s) URL).</summary>
    InvalidConfiguration,

    /// <summary>The gateway rejected the request (HTTP 401/403).</summary>
    Authentication,

    /// <summary>The gateway is rate-limiting requests (HTTP 429).</summary>
    RateLimited,

    /// <summary>The gateway is unreachable or returned an HTTP 5xx.</summary>
    Unavailable,

    /// <summary>The request exceeded the configured AI_TIMEOUT_SECONDS.</summary>
    Timeout,

    /// <summary>The gateway returned a malformed or empty chat-completion response.</summary>
    InvalidResponse,
}

public sealed class AiException : Exception
{
    public AiErrorKind Kind { get; }

    public AiException(AiErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}
