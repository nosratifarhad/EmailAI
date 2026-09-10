using EmailAI.Application.Settings;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace EmailAI.Infrastructure.Settings;

/// <summary>
/// <see cref="IExchangeSettingsService"/> for the desktop app. It is the ONLY place that
/// writes the per-user Exchange configuration, and it keeps the two halves strictly
/// separated:
///
///  * the endpoint / mode / account go to <see cref="IUserSettingsStore"/> as non-secret JSON
///    (<c>%APPDATA%\EmailAI\settings.json</c>);
///  * the password goes to <see cref="ISecretStore"/> under
///    <see cref="SecretTargets.ExchangePassword"/> (Windows Credential Manager) and is never
///    written to JSON, echoed by an API or logged.
///
/// Every request is fully validated through
/// <see cref="ExchangeOptions.GetConfigurationError(ExchangeOptions, bool)"/> BEFORE anything
/// is written, so a rejected save has no side effects. Switching to <c>Windows</c> mode
/// deletes any stored password because Windows authentication never uses one - the two modes
/// never share credentials.
/// </summary>
public sealed class ExchangeSettingsService(
    IUserSettingsStore settingsStore,
    ISecretStore secretStore,
    IExchangeConfigurationProvider configuration,
    ILogger<ExchangeSettingsService> logger) : IExchangeSettingsService
{
    public async Task<ExchangeSettingsStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var user = settings.Exchange;

        // Per-user configuration is authoritative as a whole.
        if (user is not null && !string.IsNullOrWhiteSpace(user.EwsUrl))
        {
            var mode = NormalizeMode(user.Authentication);
            var usesUsernamePassword = IsUsernamePassword(mode);

            return new ExchangeSettingsStatus(
                Configured: true,
                UserManaged: true,
                UsesDefaultCredentials: IsWindows(mode),
                EwsUrl: user.EwsUrl.Trim(),
                Authentication: mode,
                Username: usesUsernamePassword ? NullIfBlank(user.Username) : null,
                Domain: usesUsernamePassword ? NullIfBlank(user.Domain) : null,
                HasPassword: usesUsernamePassword && await HasStoredPasswordAsync(cancellationToken));
        }

        // No per-user configuration: the deployment's environment configuration is in
        // charge. Its account values are deliberately NOT surfaced (server-managed
        // credentials stay hidden) - only presence is reported, which is what the UI needs
        // to explain where the connection comes from.
        var effective = await configuration.GetEffectiveOptionsAsync(cancellationToken);
        var environmentMode = NormalizeMode(effective.Authentication);
        var configured = !string.IsNullOrWhiteSpace(effective.EwsUrl)
                         && !ExchangeOptions.IsSamplePlaceholder(effective.EwsUrl);

        return new ExchangeSettingsStatus(
            Configured: configured,
            UserManaged: false,
            UsesDefaultCredentials: effective.UsesWindowsAuthentication,
            EwsUrl: configured ? effective.EwsUrl.Trim() : null,
            Authentication: environmentMode,
            Username: null,
            Domain: null,
            HasPassword: effective.UsesUsernamePasswordAuthentication
                         && !string.IsNullOrWhiteSpace(effective.Password));
    }

    public async Task SaveAsync(ExchangeSettingsUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var settings = await settingsStore.GetAsync(cancellationToken);
        var existing = settings.Exchange;

        // The environment configuration is intentionally NOT used to complete a per-user
        // configuration: a saved configuration must be complete on its own, so credentials
        // from the two sources can never be mixed.
        var ewsUrl = RequireEwsUrl(update.EwsUrl ?? existing?.EwsUrl);
        var mode = NormalizeMode(update.Authentication ?? existing?.Authentication);
        var usesUsernamePassword = IsUsernamePassword(mode);

        var username = usesUsernamePassword
            ? NullIfBlank(update.Username) ?? NullIfBlank(existing?.Username)
            : null;
        var domain = usesUsernamePassword
            ? NullIfBlank(update.Domain) ?? NullIfBlank(existing?.Domain)
            : null;
        var password = usesUsernamePassword ? NullIfBlank(update.Password) : null;
        var hasStoredPassword = usesUsernamePassword && await HasStoredPasswordAsync(cancellationToken);

        // Validate everything (mode, username, password) before writing anything.
        var candidate = new ExchangeOptions
        {
            EwsUrl = ewsUrl,
            Authentication = mode,
            Username = username,
            Domain = domain,
            Password = password,
        };

        var error = ExchangeOptions.GetConfigurationError(candidate, passwordProvided: false);
        if (error is not null)
        {
            throw new ArgumentException(error);
        }

        if (usesUsernamePassword && password is null && !hasStoredPassword)
        {
            // A blank password input only means "keep the stored one" when there is one;
            // otherwise the canonical configuration error applies (never re-typed here).
            throw new ArgumentException(
                ExchangeOptions.GetConfigurationError(candidate)!);
        }

        var stored = new ExchangeUserSettings(ewsUrl, mode, username, domain);

        if (password is not null)
        {
            // Write the secret first: if it fails, the previous configuration stays in
            // effect unchanged instead of pointing at a password that is not there.
            await secretStore.WriteAsync(SecretTargets.ExchangePassword, password, cancellationToken);
            await settingsStore.SaveAsync(settings with { Exchange = stored }, cancellationToken);
        }
        else
        {
            await settingsStore.SaveAsync(settings with { Exchange = stored }, cancellationToken);
            if (!usesUsernamePassword)
            {
                // Windows mode never uses a password - drop any previously stored one (the
                // configuration no longer depends on it, so this order is always safe).
                await secretStore.DeleteAsync(SecretTargets.ExchangePassword, cancellationToken);
            }
        }

        logger.LogInformation(
            "Per-user Exchange settings saved (mode={AuthenticationMode}, password={PasswordState}).",
            mode,
            password is not null ? "saved" : usesUsernamePassword ? "kept" : "not used");
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (settings.Exchange is not null)
        {
            // Remove the configuration first so nothing can still depend on the secret.
            await settingsStore.SaveAsync(settings with { Exchange = null }, cancellationToken);
        }

        await secretStore.DeleteAsync(SecretTargets.ExchangePassword, cancellationToken);
        logger.LogInformation(
            "Per-user Exchange settings and the stored Exchange password were removed; " +
            "the deployment configuration is in effect again.");
    }

    public async Task<ExchangeOptions> ResolveTestOptionsAsync(
        ExchangeSettingsUpdate? update,
        CancellationToken cancellationToken = default)
    {
        // The currently effective configuration is the baseline; the draft overrides the
        // fields the user actually changed. Nothing is persisted here.
        var effective = await configuration.GetEffectiveOptionsAsync(cancellationToken);
        var mode = NormalizeMode(update?.Authentication ?? effective.Authentication);
        var usesUsernamePassword = IsUsernamePassword(mode);

        var draft = new ExchangeOptions
        {
            EwsUrl = RequireEwsUrl(update?.EwsUrl ?? effective.EwsUrl),
            Authentication = mode,
            Username = usesUsernamePassword
                ? NullIfBlank(update?.Username) ?? NullIfBlank(effective.Username)
                : null,
            Domain = usesUsernamePassword
                ? NullIfBlank(update?.Domain) ?? NullIfBlank(effective.Domain)
                : null,
            Password = usesUsernamePassword
                ? NullIfBlank(update?.Password) ?? effective.Password
                : null,
            Mailbox = effective.Mailbox,
            ExchangeVersion = effective.ExchangeVersion,
            TimeoutSeconds = effective.TimeoutSeconds,
        };

        var error = ExchangeOptions.GetConfigurationError(draft);
        if (error is not null)
        {
            throw new ArgumentException(error);
        }

        return draft;
    }

    /// <summary>
    /// Validates the endpoint: it must be an absolute http(s) URL and must not be the
    /// documentation placeholder that ships in appsettings.json.
    /// </summary>
    private static string RequireEwsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "The Exchange EWS URL is required (for example " +
                "https://mail.contoso.com/EWS/Exchange.asmx).");
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "The Exchange EWS URL must be an absolute http(s) URL (for example " +
                "https://mail.contoso.com/EWS/Exchange.asmx).");
        }

        if (ExchangeOptions.IsSamplePlaceholder(trimmed))
        {
            throw new ArgumentException(
                "The Exchange EWS URL is still the documentation example " +
                $"(https://{uri.Host}). Enter the real Exchange EWS endpoint.");
        }

        return trimmed;
    }

    private async Task<bool> HasStoredPasswordAsync(CancellationToken cancellationToken)
    {
        var password = await secretStore.ReadAsync(SecretTargets.ExchangePassword, cancellationToken);
        return !string.IsNullOrWhiteSpace(password);
    }

    private static string NormalizeMode(string? stored)
    {
        var value = stored?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return ExchangeOptions.WindowsMode;
        }

        if (IsWindows(value))
        {
            return ExchangeOptions.WindowsMode;
        }

        return IsUsernamePassword(value) ? ExchangeOptions.UsernamePasswordMode : value;
    }

    private static bool IsWindows(string mode)
        => string.Equals(mode, ExchangeOptions.WindowsMode, StringComparison.OrdinalIgnoreCase);

    private static bool IsUsernamePassword(string mode)
        => string.Equals(mode, ExchangeOptions.UsernamePasswordMode, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
