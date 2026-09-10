using System.Text.Json;
using EmailAI.Application.AI;

namespace EmailAI.Api.Web;

/// <summary>
/// Options body for AI operations. The output language is optional and defaults to Auto (follow
/// the source email language); English/Persian force it explicitly.
///
/// On the wire the language is its NAME - <c>{"language":"English"}</c> - which is the
/// documented contract and what the Blazor UI sends. A numeric value
/// (<c>{"language":1}</c>) is still accepted for compatibility with earlier clients. The token is
/// kept as a <see cref="JsonElement"/> and resolved by <see cref="AiLanguageRequest"/>, so an
/// unsupported value answers with a descriptive 400 instead of the framework's empty one.
/// </summary>
public sealed class AiOperationRequest
{
    /// <summary>
    /// Requested output language: <c>"Auto"</c>, <c>"English"</c> or <c>"Persian"</c>
    /// (case-insensitive), or its numeric value (0/1/2). Omitted means Auto.
    /// </summary>
    public JsonElement? Language { get; init; }

    /// <summary>Creates an options body for one explicit language (wire form: the language name).</summary>
    public static AiOperationRequest For(AiLanguage language)
        => new() { Language = JsonSerializer.SerializeToElement(language.ToString()) };
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
