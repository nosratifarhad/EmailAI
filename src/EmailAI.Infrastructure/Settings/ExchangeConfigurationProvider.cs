using EmailAI.Application.Exceptions;
using EmailAI.Application.Settings;
using EmailAI.Domain.Exchange;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EmailAI.Infrastructure.Settings;

/// <summary>
/// <see cref="IExchangeConfigurationProvider"/> for the desktop app: the per-user
/// configuration saved in Settings (<c>%APPDATA%\EmailAI\settings.json</c> + the
/// <c>EmailAI/Exchange</c> credential) wins as a WHOLE over the deployment's
/// environment/appsettings configuration, which stays available as a
/// backward-compatible bootstrap when the user has not configured Exchange yet.
///
/// Precedence (never a per-field merge, so credentials from the two sources are never
/// mixed and no authentication fallback appears):
///   * per-user section present  - endpoint/mode/account come from the file and the
///     password (UsernamePassword mode only) from <see cref="SecretTargets.ExchangePassword"/>.
///     The password configured for the deployment is deliberately ignored.
///   * no per-user section       - the environment snapshot is used unchanged,
///     including <c>EXCHANGE_PASSWORD</c>.
///
/// The deployment snapshot still supplies the knobs the Settings page does not edit
/// (mailbox, Exchange version, timeout), so behaviour outside the user-facing settings
/// stays exactly as the administrator configured it. No value is ever logged: the
/// returned snapshot contains the password because EWS must authenticate with it, and
/// it is never cached, logged or returned to a client.
/// </summary>
public sealed class ExchangeConfigurationProvider(
    IUserSettingsStore settingsStore,
    ISecretStore secretStore,
    IOptionsMonitor<ExchangeOptions> environment,
    ILogger<ExchangeConfigurationProvider> logger) : IExchangeConfigurationProvider
{
    public async Task<ExchangeOptions> GetEffectiveOptionsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var user = settings.Exchange;

        // A stored section without an endpoint is "not configured" - the deployment
        // configuration stays in charge.
        if (user is null || string.IsNullOrWhiteSpace(user.EwsUrl))
        {
            return environment.CurrentValue;
        }

        var current = environment.CurrentValue;
        var authentication = NormalizeMode(user.Authentication);
        var usesUsernamePassword = string.Equals(
            authentication, ExchangeOptions.UsernamePasswordMode, StringComparison.OrdinalIgnoreCase);

        string? password = null;
        if (usesUsernamePassword)
        {
            password = await ReadStoredPasswordAsync(cancellationToken);
        }

        logger.LogDebug(
            "Exchange is configured per user (mode={AuthenticationMode}); the deployment " +
            "environment configuration is not used for this account.",
            authentication);

        return new ExchangeOptions
        {
            EwsUrl = user.EwsUrl.Trim(),
            Authentication = authentication,
            Username = usesUsernamePassword ? NullIfBlank(user.Username) : null,
            Domain = usesUsernamePassword ? NullIfBlank(user.Domain) : null,
            Password = password,
            // Deployment-owned knobs keep their configured values.
            Mailbox = current.Mailbox,
            ExchangeVersion = current.ExchangeVersion,
            TimeoutSeconds = current.TimeoutSeconds,
        };
    }

    /// <summary>
    /// Reads the per-user Exchange password. A credential-store failure becomes a typed
    /// configuration error (never a raw platform exception) so mail operations and the
    /// connection test report an actionable, secret-free message.
    /// </summary>
    private async Task<string?> ReadStoredPasswordAsync(CancellationToken cancellationToken)
    {
        try
        {
            var password = await secretStore.ReadAsync(SecretTargets.ExchangePassword, cancellationToken);
            return string.IsNullOrWhiteSpace(password) ? null : password;
        }
        catch (SecretStoreException exception)
        {
            logger.LogError(exception,
                "The per-user Exchange password could not be read from the credential store.");
            throw new ExchangeMailException(
                ExchangeMailErrorKind.Configuration,
                "Exchange is configured per user, but the stored Exchange password could not be " +
                "read from the Windows Credential Manager. Sign in to Windows and save the " +
                "password again in Settings.",
                exception);
        }
    }

    /// <summary>
    /// Canonicalizes the stored mode so the two supported modes always reach the EWS layer
    /// in their documented spelling. A blank value falls back to the documented default
    /// (Windows), while an unrecognized value is passed through deliberately: validation
    /// then reports it as a clear configuration error instead of silently switching
    /// authentication mechanisms.
    /// </summary>
    private static string NormalizeMode(string? stored)
    {
        var value = stored?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return ExchangeOptions.WindowsMode;
        }

        if (value.Equals(ExchangeOptions.WindowsMode, StringComparison.OrdinalIgnoreCase))
        {
            return ExchangeOptions.WindowsMode;
        }

        return value.Equals(ExchangeOptions.UsernamePasswordMode, StringComparison.OrdinalIgnoreCase)
            ? ExchangeOptions.UsernamePasswordMode
            : value;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
