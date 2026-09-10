using EmailAI.Domain.Exchange;

namespace EmailAI.Application.Settings;

/// <summary>
/// Resolves the EFFECTIVE Exchange connection settings at runtime: the per-user settings
/// saved in Settings (<c>%APPDATA%\EmailAI\settings.json</c>) when they exist, otherwise the
/// deployment's environment/appsettings configuration as a backward-compatible bootstrap.
///
/// Precedence rule (deliberately NOT a per-field merge):
///   * a complete per-user Exchange configuration is authoritative for the endpoint, the
///     authentication mode, the username and the domain, and the password is taken from the
///     per-user secret store only;
///   * with no per-user configuration the environment configuration is used as a whole,
///     including <c>EXCHANGE_PASSWORD</c>.
///
/// Credentials from the two sources are therefore never silently mixed, and no automatic
/// authentication fallback is ever introduced. The returned snapshot contains the password
/// (when the mode needs one) because the EWS layer must authenticate with it; it is never
/// logged, cached or returned to a client.
/// </summary>
public interface IExchangeConfigurationProvider
{
    /// <summary>Returns a snapshot of the effective Exchange settings.</summary>
    Task<ExchangeOptions> GetEffectiveOptionsAsync(CancellationToken cancellationToken = default);
}
