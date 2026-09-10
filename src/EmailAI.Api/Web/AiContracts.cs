using EmailAI.Application.AI;

namespace EmailAI.Api.Web;

/// <summary>
/// Options body for AI operations. Language defaults to Auto (follow the source
/// email language); English/Persian override the output language explicitly.
/// </summary>
public sealed class AiOperationRequest
{
    public AiLanguage Language { get; init; } = AiLanguage.Auto;
}

/// <summary>Response body of every AI operation: the produced text, nothing else.</summary>
public sealed class AiContentResult
{
    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// Shape of GET /api/ai/readiness - whether the AI assistant can be used right now, and why
/// not when it cannot. The message/action are ready to show to the user; no key, no header,
/// no provider internals are ever part of this response.
/// </summary>
public sealed class AiReadinessResponse
{
    /// <summary>True only when an AI operation may be attempted.</summary>
    public bool Ready { get; init; }

    /// <summary>Machine-readable state name (see <c>AiReadinessStatus</c>).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Machine-readable code (same vocabulary as the REST error envelope).</summary>
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>User-facing explanation of what is missing or unavailable.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>User-facing action that fixes it (for example "Open Settings").</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>True when the user can fix this in Settings.</summary>
    public bool CanOpenSettings { get; init; }
}
