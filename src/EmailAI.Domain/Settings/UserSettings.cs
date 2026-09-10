using EmailAI.Domain.AI;

namespace EmailAI.Domain.Settings;

/// <summary>
/// The NON-SECRET per-user Exchange connection settings saved from the Settings page and
/// persisted per Windows user under <c>%APPDATA%\EmailAI\settings.json</c>. A record is
/// only produced when <see cref="EwsUrl"/> is a real endpoint - a stored section without
/// an endpoint is treated as "not configured".
///
/// The Exchange password is deliberately NOT part of this record: it is a secret and lives
/// exclusively in the per-user credential store (Windows Credential Manager target
/// <c>EmailAI/Exchange</c>), never in JSON, appsettings, logs or API responses.
/// </summary>
/// <param name="EwsUrl">EWS endpoint the user configured (absolute http(s) URL).</param>
/// <param name="Authentication">Explicit mode: <c>Windows</c> or <c>UsernamePassword</c>.</param>
/// <param name="Username">Explicit account, required only in <c>UsernamePassword</c> mode.</param>
/// <param name="Domain">Optional account domain (only meaningful with an explicit account).</param>
public sealed record ExchangeUserSettings(
    string EwsUrl,
    string Authentication,
    string? Username,
    string? Domain);

/// <summary>
/// The complete non-secret per-user configuration document persisted as
/// <c>%APPDATA%\EmailAI\settings.json</c>:
///
/// <code>
/// {
///   "exchange": { "ewsUrl": "...", "authentication": "Windows", "username": null, "domain": null },
///   "ai":       { "baseUrl": "...", "model": "..." }
/// }
/// </code>
///
/// Secrets never appear here: the Exchange password and the AI API key live in the
/// per-user credential store only. A section is null when the user has not configured that
/// domain yet, which keeps the two domains fully independent (configuring Exchange is
/// never required to configure AI and vice versa).
/// </summary>
/// <param name="Exchange">Per-user Exchange settings, or null when not configured.</param>
/// <param name="Ai">Per-user AI provider overrides, or null when not configured.</param>
public sealed record UserSettings(ExchangeUserSettings? Exchange, AiUserSettings? Ai)
{
    /// <summary>The empty document used when no per-user settings are stored.</summary>
    public static UserSettings Empty { get; } = new(null, null);
}
