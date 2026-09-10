using EmailAI.Domain.Exchange;

namespace EmailAI.Api.Web;

/// <summary>
/// Shape of GET /api/settings/ai - the EFFECTIVE AI provider settings plus key
/// presence. Deliberately contains NO API key (not even a masked or truncated
/// version): the browser only learns whether a key exists. The base URL/model are
/// the effective values (server configuration merged with per-user Settings
/// overrides) - never a secret.
/// </summary>
public sealed class AiSettingsResponse
{
    public bool Configured { get; init; }

    public bool HasApiKey { get; init; }

    /// <summary>False when this deployment sources the key from the environment.</summary>
    public bool UserManaged { get; init; }

    /// <summary>"windows" | "environment" - the effective key source right now.</summary>
    public string EffectiveSource { get; init; } = string.Empty;

    public string? BaseUrl { get; init; }

    public string? Model { get; init; }

    public int TimeoutSeconds { get; init; }
}

/// <summary>
/// Body of PUT /api/settings/ai. Every field is optional; only the fields that are
/// present (non-null) are applied:
///   baseUrl - stores the per-user override; an empty string clears it (the server
///             configuration is used again). Never contains a secret.
///   model   - stores the per-user model override; an empty string clears it.
///   apiKey  - when non-empty, replaces the key in the per-user secure store. The key
///             is accepted here and NEVER echoed back by any endpoint.
/// </summary>
public sealed class AiSettingsUpdateRequest
{
    public string? BaseUrl { get; init; }

    public string? Model { get; init; }

    public string? ApiKey { get; init; }
}

/// <summary>Shape of POST /api/settings/ai/test (always HTTP 200 with a status).</summary>
public sealed class AiTestConnectionResponse
{
    /// <summary>
    /// "connected" | "not_configured" | "authentication_failed" | "rate_limited" |
    /// "timed_out" | "unavailable".
    /// </summary>
    public string Status { get; init; } = string.Empty;

    public long? LatencyMs { get; init; }

    /// <summary>Sanitized, key-free explanation shown only when not connected.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Shape of GET /api/settings/exchange - where the effective Exchange configuration comes
/// from, the explicit authentication mode, the (non-secret) per-user endpoint/account, and
/// whether a password is stored. The automatically detected Windows account is included as
/// informational display data.
///
/// Secret rules: no password, and no server-managed account value, is ever returned. Only
/// per-user values the user typed in Settings are echoed back, and credential presence is
/// reported as booleans only.
/// </summary>
public sealed class ExchangeSettingsResponse
{
    /// <summary>True when an endpoint is configured (per-user or by the deployment).</summary>
    public bool Configured { get; init; }

    /// <summary>
    /// True when this Windows account configured Exchange in Settings (authoritative);
    /// false when the deployment's environment configuration is in effect.
    /// </summary>
    public bool UserManaged { get; init; }

    /// <summary>The effective, non-secret EWS endpoint (null when not configured).</summary>
    public string? EwsUrl { get; init; }

    /// <summary>Configured Exchange authentication mode ("Windows" | "UsernamePassword").</summary>
    public string AuthenticationMode { get; init; } = string.Empty;

    /// <summary>True when Exchange authenticates with the current Windows identity (Windows mode).</summary>
    public bool UsesDefaultCredentials { get; init; }

    /// <summary>
    /// The per-user account, returned only when the user manages Exchange here and the mode
    /// is UsernamePassword. The deployment's account is never surfaced.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>The per-user account domain (same rules as <see cref="Username"/>).</summary>
    public string? Domain { get; init; }

    /// <summary>True when a password is stored for this account. Never reveals the value.</summary>
    public bool HasPassword { get; init; }

    /// <summary>True when a username is configured for the effective UsernamePassword mode. Never reveals the value.</summary>
    public bool UsernameConfigured { get; init; }

    /// <summary>True when a password is configured for the effective UsernamePassword mode. Never reveals the value.</summary>
    public bool PasswordConfigured { get; init; }

    public DetectedIdentityDto? DetectedIdentity { get; init; }

    /// <summary>
    /// The mailbox identity EmailAI resolved as the CURRENT USER (display name / SMTP address
    /// and how it was established). This is what the AI is told when it interprets an email -
    /// it contains no credential and, when not yet resolved, is null.
    /// </summary>
    public CurrentUserDto? CurrentUser { get; init; }

    /// <summary>Human-readable, environment-specific guidance (never contains secrets).</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// Body of PUT /api/settings/exchange and of the optional draft sent to
/// POST /api/settings/exchange/test. Every field is optional; null means "leave unchanged"
/// (for the test draft: "use the currently effective value"):
///   ewsUrl         - absolute http(s) EWS endpoint (required for a save).
///   authentication - "Windows" or "UsernamePassword" (case-insensitive).
///   username       - explicit account; required in UsernamePassword mode.
///   domain         - optional account domain for UsernamePassword mode.
///   password       - accepted here, stored ONLY in the per-user credential store and never
///                    echoed back. Blank on a save keeps an already stored password.
/// </summary>
public sealed class ExchangeSettingsUpdateRequest
{
    public string? EwsUrl { get; init; }

    public string? Authentication { get; init; }

    public string? Username { get; init; }

    public string? Domain { get; init; }

    public string? Password { get; init; }
}

/// <summary>Shape of POST /api/settings/exchange/test (always HTTP 200 with a status).</summary>
public sealed class ExchangeTestConnectionResponse
{
    /// <summary>"connected" | "not_configured" | "failed".</summary>
    public string Status { get; init; } = string.Empty;

    public long? LatencyMs { get; init; }

    /// <summary>Sanitized, secret-free explanation shown only when the probe failed.</summary>
    public string? Error { get; init; }
}

public sealed class DetectedIdentityDto
{
    public string? UserName { get; init; }

    public string? Domain { get; init; }

    public string? FullName { get; init; }

    public string DetectionSource { get; init; } = string.Empty;
}

/// <summary>
/// The current user as resolved from the Exchange context. Display data only: no password, no
/// token. <see cref="Source"/> tells the UI how authoritative it is ("ExchangeDirectory" is
/// resolved through Exchange, "ConfiguredMailbox"/"ConfiguredAccount"/"WindowsIdentity" are
/// local fallbacks, "Unknown" means nothing could be determined).
/// </summary>
public sealed class CurrentUserDto
{
    public string? DisplayName { get; init; }

    public string? SmtpAddress { get; init; }

    public string? AccountName { get; init; }

    public string Source { get; init; } = string.Empty;

    /// <summary>True when at least one identifying value is known and safe to disclose.</summary>
    public bool Resolved { get; init; }
}

internal static class SettingsContractMapping
{
    public static DetectedIdentityDto ToDto(DetectedWindowsIdentity identity) => new()
    {
        UserName = identity.UserName,
        Domain = identity.Domain,
        FullName = identity.FullName,
        DetectionSource = identity.DetectionSource,
    };

    /// <summary>
    /// Projects the resolved current-user identity for the UI. The identity is display data
    /// (no password, no token), but a value the DEPLOYMENT configured - a service account the
    /// user never typed - is never echoed back; that is the same rule the Exchange account
    /// fields follow. Values resolved through the Exchange directory (display name, SMTP
    /// address) are shown. The AI itself always receives the full identity server-side.
    /// </summary>
    /// <param name="identity">The resolved mailbox identity.</param>
    /// <param name="hiddenValues">
    /// Values that must not be disclosed (empty/null when the configuration is per-user, in
    /// which case the user's own values are safe to echo back).
    /// </param>
    public static CurrentUserDto ToDto(MailboxIdentity identity, IReadOnlyCollection<string>? hiddenValues = null)
    {
        var displayName = Disclosable(identity.DisplayName);
        var smtpAddress = Disclosable(identity.SmtpAddress);
        var accountName = Disclosable(identity.AccountName);

        return new CurrentUserDto
        {
            DisplayName = displayName,
            SmtpAddress = smtpAddress,
            AccountName = accountName,
            Source = identity.Source,
            Resolved = displayName is not null || smtpAddress is not null || accountName is not null,
        };

        string? Disclosable(string? value)
            => value is not null
               && hiddenValues is not null
               && hiddenValues.Any(hidden => string.Equals(hidden, value, StringComparison.OrdinalIgnoreCase))
                ? null
                : value;
    }
}
