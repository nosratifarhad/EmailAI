using EmailAI.Domain.Exchange;

namespace EmailAI.Application.Settings;

/// <summary>
/// Non-secret snapshot of the per-user Exchange configuration, safe to return from a GET
/// API endpoint. It never contains the password - only <see cref="HasPassword"/> reports
/// whether one is stored.
///
/// <see cref="UserManaged"/> distinguishes the two supported configuration sources:
///   * true  - the user configured Exchange in Settings (<c>%APPDATA%\EmailAI\settings.json</c>);
///             this configuration is AUTHORITATIVE and the endpoint/mode/account values below
///             are the per-user ones.
///   * false - no per-user configuration exists, so the deployment's environment
///             configuration is in effect (backward-compatible bootstrap). In that case the
///             account values are deliberately NOT surfaced (server-managed credentials stay
///             hidden) and only credential presence is reported.
/// </summary>
public sealed record ExchangeSettingsStatus(
    bool Configured,
    bool UserManaged,
    bool UsesDefaultCredentials,
    string? EwsUrl,
    string Authentication,
    string? Username,
    string? Domain,
    bool HasPassword);

/// <summary>
/// Body of PUT /api/settings/exchange (and of the optional draft passed to the connection
/// test). Every value is optional; the password is accepted here and NEVER echoed back or
/// persisted to JSON.
/// </summary>
/// <param name="EwsUrl">Absolute http(s) EWS endpoint. Required for a save.</param>
/// <param name="Authentication">"Windows" or "UsernamePassword" (case-insensitive).</param>
/// <param name="Username">Explicit account; required in UsernamePassword mode.</param>
/// <param name="Domain">Optional account domain for UsernamePassword mode.</param>
/// <param name="Password">
/// Explicit password; stored in the per-user credential store only. Null/blank on a save
/// keeps the already-stored password; blank with nothing stored is rejected in
/// UsernamePassword mode.
/// </param>
public sealed record ExchangeSettingsUpdate(
    string? EwsUrl = null,
    string? Authentication = null,
    string? Username = null,
    string? Domain = null,
    string? Password = null);

/// <summary>
/// Management surface for the per-user Exchange configuration used by the Settings UI/API.
/// Sits between the UI and the concrete settings/secret stores so the UI never touches the
/// Windows Credential Manager directly. Never returns the password.
/// </summary>
public interface IExchangeSettingsService
{
    /// <summary>Returns the current non-secret Exchange settings + password presence.</summary>
    Task<ExchangeSettingsStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the per-user Exchange configuration: the endpoint/mode/account go to
    /// <see cref="IUserSettingsStore"/>, the password (when supplied) to
    /// <see cref="ISecretStore"/>. Everything is validated BEFORE anything is written, so a
    /// rejected request has no side effects.
    ///
    /// Throws <see cref="ArgumentException"/> for an invalid/missing EWS URL, an
    /// unsupported authentication mode, a missing username or a missing password.
    /// Switching to <c>Windows</c> mode removes any stored password because Windows mode
    /// never uses one.
    /// </summary>
    Task SaveAsync(ExchangeSettingsUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the per-user Exchange configuration AND the stored Exchange password, so the
    /// deployment falls back to its environment configuration (or reports "not configured").
    /// </summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the <see cref="ExchangeOptions"/> a connection test should use: a draft
    /// built from <paramref name="update"/> where a value is supplied, otherwise the
    /// currently effective configuration. Never persists anything.
    /// </summary>
    Task<ExchangeOptions> ResolveTestOptionsAsync(
        ExchangeSettingsUpdate? update,
        CancellationToken cancellationToken = default);
}
