using EmailAI.Application.Exceptions;
using EmailAI.Domain.Exchange;
using Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// Builds a fresh <see cref="ExchangeService"/> per authentication attempt from the
/// current <see cref="ExchangeOptions"/>. Constructing the object is cheap (no network
/// I/O happens until the first call), so a new instance per operation/attempt is fine
/// and always reflects the latest configuration.
/// </summary>
internal static class ExchangeServiceFactory
{
    public static ExchangeService Create(ExchangeOptions options, ExchangeAuthMode authentication)
    {
        if (string.IsNullOrWhiteSpace(options.EwsUrl))
        {
            throw new ExchangeMailException(
                ExchangeMailErrorKind.Configuration,
                "Exchange is not configured: the EWS URL is empty. Configure the Exchange " +
                "endpoint in Settings (or set the EXCHANGE_EWS_URL environment variable) " +
                "before using mail endpoints.");
        }

        if (!Uri.TryCreate(options.EwsUrl.Trim(), UriKind.Absolute, out var ewsUri)
            || (ewsUri.Scheme != Uri.UriSchemeHttp && ewsUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ExchangeMailException(
                ExchangeMailErrorKind.Configuration,
                $"Exchange:EwsUrl '{options.EwsUrl}' is not a valid http(s) endpoint.");
        }

        if (ExchangeOptions.IsSamplePlaceholder(options.EwsUrl))
        {
            // appsettings.json keeps a documentation-only value; a sample host must
            // never be used as a real endpoint just because nothing real was configured.
            throw new ExchangeMailException(
                ExchangeMailErrorKind.Configuration,
                "Exchange:EwsUrl still points at the reserved example host mail.example.com. " +
                "Configure the real Exchange EWS endpoint in Settings (or set EXCHANGE_EWS_URL) - " +
                "the appsettings.json value is a public placeholder, not runtime configuration.");
        }

        // Explicit mode/credential validation (single source of truth on ExchangeOptions) so
        // every caller reports exactly the same, secret-free configuration error.
        var configurationError = ExchangeOptions.GetConfigurationError(options);
        if (configurationError is not null)
        {
            throw new ExchangeMailException(ExchangeMailErrorKind.Configuration, configurationError);
        }

        if (!Enum.TryParse<ExchangeVersion>(options.ExchangeVersion, ignoreCase: true, out var version))
        {
            throw new ExchangeMailException(
                ExchangeMailErrorKind.Configuration,
                $"Exchange:ExchangeVersion '{options.ExchangeVersion}' is not supported. " +
                $"Expected one of: {string.Join(", ", Enum.GetNames<ExchangeVersion>())}.");
        }

        var service = new ExchangeService(version)
        {
            Url = ewsUri,
            Timeout = Math.Clamp(options.TimeoutSeconds, 1, 3600) * 1000,
            UserAgent = "EmailAI/0.1",
        };

        switch (authentication)
        {
            case ExchangeAuthMode.Windows:
                // Windows Integrated Authentication: the EWS server authenticates the
                // current Windows/process identity. Explicit credentials are never used
                // and there is no automatic fallback to them.
                service.UseDefaultCredentials = true;
                break;

            case ExchangeAuthMode.UsernamePassword:
                // Explicit username/password over WebCredentials (the EWS server negotiates
                // the scheme, typically NTLM/Kerberos or Basic). The Windows identity is not
                // used and never attempted as a fallback. Credentials come from the per-user
                // credential store in Settings (or EXCHANGE_DOMAIN / EXCHANGE_USERNAME /
                // EXCHANGE_PASSWORD for a server/environment deployment) only.
                //
                // Defensive guard: GetConfigurationError above already validated this, so it
                // only fires if the factory is ever called directly with mismatched inputs.
                if (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password))
                {
                    throw new ExchangeMailException(
                        ExchangeMailErrorKind.Configuration,
                        ExchangeOptions.GetConfigurationError(options)
                        ?? "Exchange:Username and Exchange:Password are both required for " +
                           "UsernamePassword authentication (set them in Settings, or via the " +
                           "EXCHANGE_USERNAME / EXCHANGE_PASSWORD environment variables).");
                }

                service.Credentials = string.IsNullOrWhiteSpace(options.Domain)
                    ? new WebCredentials(options.Username, options.Password!)
                    : new WebCredentials(options.Username, options.Password!, options.Domain);
                break;

            default:
                throw new ExchangeMailException(
                    ExchangeMailErrorKind.Configuration,
                    $"Unsupported Exchange authentication mode '{authentication}'.");
        }

        if (!string.IsNullOrWhiteSpace(options.Mailbox))
        {
            // Optional: open another user's mailbox via EWS impersonation.
            service.ImpersonatedUserId = new ImpersonatedUserId(
                ConnectingIdType.SmtpAddress,
                options.Mailbox.Trim());
        }

        return service;
    }
}
