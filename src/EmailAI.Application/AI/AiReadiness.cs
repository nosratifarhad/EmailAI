namespace EmailAI.Application.AI;

/// <summary>
/// Why the AI assistant can (or cannot) be used right now. The UI maps these states to a
/// single, non-technical explanation plus the action that fixes it - the user must never be
/// left with a raw HTTP/exception message. Nothing here carries a credential.
/// </summary>
public enum AiReadinessStatus
{
    /// <summary>Provider, key and model are present and (when probed) the provider answered.</summary>
    Ready = 0,

    /// <summary>No AI provider is configured at all (no base URL/model).</summary>
    NotConfigured = 1,

    /// <summary>A provider is configured for this account but no API key is stored yet.</summary>
    MissingApiKey = 2,

    /// <summary>A provider is configured but no model is selected.</summary>
    MissingModel = 3,

    /// <summary>Configuration exists but is unusable (for example a non-http base URL).</summary>
    InvalidConfiguration = 4,

    /// <summary>The provider could not be reached (DNS/connection refused/5xx/timeout of the probe).</summary>
    Unreachable = 5,

    /// <summary>The provider rejected the credentials (HTTP 401/403).</summary>
    AuthenticationFailed = 6,

    /// <summary>The provider is rate-limiting requests (HTTP 429).</summary>
    RateLimited = 7,

    /// <summary>The provider did not answer within the configured timeout.</summary>
    Timeout = 8,

    /// <summary>The request reached the provider but failed in another way.</summary>
    RequestFailed = 9,
}

/// <summary>
/// One AI readiness verdict: the machine-readable <see cref="Status"/> (also exposed to the
/// UI as <see cref="ErrorCode"/>) plus a user-facing, secret-free explanation and the
/// suggested action. It contains no key, no header and no internal detail.
/// </summary>
/// <param name="Status">The state to distinguish.</param>
/// <param name="Message">User-facing sentence explaining what is missing/unavailable.</param>
/// <param name="Action">User-facing action that fixes it (for example "Open Settings").</param>
/// <param name="CanOpenSettings">
/// True when the user can fix this in Settings (a read-only environment-managed deployment
/// cannot) - the UI only offers the navigation when it can actually help.
/// </param>
/// <param name="ErrorCode">Machine-readable code (same vocabulary as the REST error envelope).</param>
public sealed record AiReadiness(
    AiReadinessStatus Status,
    string Message,
    string Action,
    bool CanOpenSettings,
    string ErrorCode)
{
    /// <summary>True only when an AI operation may be attempted.</summary>
    public bool IsReady => Status == AiReadinessStatus.Ready;
}

/// <summary>
/// Inspects the AI state BEFORE an AI operation is attempted, so the UI can explain what is
/// missing instead of executing a request that is guaranteed to fail.
/// </summary>
public interface IAiReadinessService
{
    /// <summary>
    /// Returns the current AI readiness. <paramref name="probe"/> additionally contacts the
    /// configured provider (GET {BaseUrl}/models) so "endpoint unreachable" and
    /// "credentials rejected" can be distinguished from "not configured"; without it no
    /// network request is made (cheap, safe to call before every AI action).
    /// </summary>
    Task<AiReadiness> GetReadinessAsync(bool probe, CancellationToken cancellationToken = default);
}

/// <summary>
/// The single place that turns an AI state/failure into human guidance. Used by
/// <see cref="AiReadinessService"/> (before an operation) and by the UI (after a failed
/// operation, from the REST error code) so both always say the same thing.
/// </summary>
public static class AiUserMessages
{
    public const string OpenSettingsAction = "Open Settings";
    public const string CheckSettingsAction = "Check Settings";
    public const string TryAgainAction = "Try again";

    /// <summary>Guidance for a known readiness state.</summary>
    public static AiReadiness ForStatus(AiReadinessStatus status, bool canOpenSettings) => status switch
    {
        AiReadinessStatus.Ready => new AiReadiness(
            status,
            "The AI assistant is ready.",
            string.Empty,
            canOpenSettings,
            "ai_ready"),

        AiReadinessStatus.NotConfigured => new AiReadiness(
            status,
            "AI Assistant is not ready. No AI service is configured yet - connect your AI " +
            "service and choose a model in Settings.",
            OpenSettingsAction,
            canOpenSettings,
            "ai_not_configured"),

        AiReadinessStatus.MissingApiKey => new AiReadiness(
            status,
            "AI Assistant is not ready. Your AI service is configured but no API key is saved " +
            "for this Windows account yet.",
            OpenSettingsAction,
            canOpenSettings,
            "ai_not_configured"),

        AiReadinessStatus.MissingModel => new AiReadiness(
            status,
            "AI Assistant is not ready. No model is selected for your AI service.",
            OpenSettingsAction,
            canOpenSettings,
            "ai_not_configured"),

        AiReadinessStatus.InvalidConfiguration => new AiReadiness(
            status,
            "AI Assistant is not ready. The configured AI Base URL is not a valid http(s) address.",
            OpenSettingsAction,
            canOpenSettings,
            "ai_invalid_configuration"),

        AiReadinessStatus.Unreachable => new AiReadiness(
            status,
            "The AI service cannot be reached right now. Check the Base URL and your network " +
            "connection, then test the connection in Settings.",
            CheckSettingsAction,
            canOpenSettings,
            "ai_unavailable"),

        AiReadinessStatus.AuthenticationFailed => new AiReadiness(
            status,
            "The AI service rejected the saved API key. Save a valid key in Settings.",
            CheckSettingsAction,
            canOpenSettings,
            "ai_authentication_failed"),

        AiReadinessStatus.RateLimited => new AiReadiness(
            status,
            "The AI service is rate-limiting requests right now. Please try again in a moment.",
            TryAgainAction,
            false,
            "ai_rate_limited"),

        AiReadinessStatus.Timeout => new AiReadiness(
            status,
            "The AI service did not answer in time. Please try again.",
            TryAgainAction,
            canOpenSettings,
            "ai_timeout"),

        _ => new AiReadiness(
            status,
            "The AI request could not be completed. Please try again.",
            TryAgainAction,
            canOpenSettings,
            "ai_invalid_response"),
    };

    /// <summary>
    /// Guidance for a failed AI operation, mapped from the REST error code so a technical
    /// status code never reaches the user. Unknown codes keep a bounded, non-technical
    /// fallback sentence (never a stack trace or an internal message).
    /// </summary>
    public static AiReadiness ForErrorCode(string? errorCode, bool canOpenSettings = true) => errorCode switch
    {
        "ai_not_configured" => ForStatus(AiReadinessStatus.NotConfigured, canOpenSettings),
        "ai_invalid_configuration" => ForStatus(AiReadinessStatus.InvalidConfiguration, canOpenSettings),
        "ai_authentication_failed" => ForStatus(AiReadinessStatus.AuthenticationFailed, canOpenSettings),
        "ai_rate_limited" => ForStatus(AiReadinessStatus.RateLimited, canOpenSettings),
        "ai_unavailable" => ForStatus(AiReadinessStatus.Unreachable, canOpenSettings),
        "ai_timeout" => ForStatus(AiReadinessStatus.Timeout, canOpenSettings),
        "ai_credential_store_failed" => new AiReadiness(
            AiReadinessStatus.MissingApiKey,
            "The saved AI API key could not be read from Windows Credential Manager. " +
            "Sign in to Windows and save the key again in Settings.",
            OpenSettingsAction,
            canOpenSettings,
            "ai_credential_store_failed"),
        _ => new AiReadiness(
            AiReadinessStatus.RequestFailed,
            "The AI request could not be completed. The mail client is unaffected - please try again.",
            TryAgainAction,
            canOpenSettings,
            errorCode ?? "ai_request_failed"),
    };
}
