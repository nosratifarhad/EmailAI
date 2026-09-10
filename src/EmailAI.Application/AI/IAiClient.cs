namespace EmailAI.Application.AI;

/// <summary>Role of one chat message (OpenAI-compatible "system"/"user"/"assistant").</summary>
public enum AiChatRole
{
    System,
    User,
    Assistant,
}

/// <summary>One chat message with a role and content.</summary>
public sealed record AiChatMessage(AiChatRole Role, string Content);

/// <summary>
/// A chat-completion request. <see cref="Model"/> is optional: when null the configured
/// default model is used (request-level override is supported for tests/tools).
/// </summary>
public sealed record AiChatRequest(
    IReadOnlyList<AiChatMessage> Messages,
    string? Model = null,
    double? Temperature = null);

/// <summary>The text completion returned by the model.</summary>
public sealed record AiChatCompletion(string Content, string? Model, string? FinishReason);

/// <summary>Result of a lightweight connectivity probe (no prompt is sent).</summary>
public sealed record AiProbeResult(bool IsAvailable, long? LatencyMs, string? Error, AiErrorKind? ErrorKind = null);

/// <summary>
/// The low-level, provider-agnostic chat client used by the application services.
/// Implementations speak OpenAI-compatible HTTP to the configured provider.
/// No business prompts live behind this interface - keep it reusable.
/// </summary>
public interface IAiClient
{
    /// <summary>
    /// Sends one chat-completion request and returns the assistant text.
    /// Throws <see cref="AiException"/> for configuration, transport, HTTP and
    /// parsing failures. OperationCanceledException propagates when the caller's
    /// token is cancelled (user navigation/cancel).
    /// </summary>
    Task<AiChatCompletion> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Lightweight reachability check (e.g. GET /models) that never sends a prompt.
    /// Returns a result rather than throwing for gateway failures; throws
    /// <see cref="AiException"/> only when AI is not configured at all.
    /// </summary>
    Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken);
}
