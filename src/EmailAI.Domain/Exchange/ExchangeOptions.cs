namespace EmailAI.Domain.Exchange;

/// <summary>
/// Server-side Exchange connection settings. Bound from appsettings.json
/// and overridden by environment variables (see .env.example).
/// Passwords live only in server configuration — never in the browser.
/// No credentials are baked into the code or configuration defaults.
/// </summary>
public sealed class ExchangeOptions
{
    public const string SectionName = "Exchange";

    public string EwsUrl { get; set; } = string.Empty;

    /// <summary>
    /// Exchange authentication mode. Explicit, single-mode only:
    ///   "Windows"          - authenticate with the current Windows/process identity
    ///                        (UseDefaultCredentials). No username/password is needed
    ///                        and no other credential mechanism is ever tried.
    ///   "UsernamePassword"  - authenticate with the configured EXCHANGE_DOMAIN /
    ///                        EXCHANGE_USERNAME / EXCHANGE_PASSWORD only. The Windows
    ///                        identity is not used and never tried as a fallback.
    /// There is deliberately NO automatic Windows -&gt; username/password (or reverse)
    /// fallback: a failed attempt is a clear authentication error.
    /// </summary>
    public string Authentication { get; set; } = WindowsMode;

    public string? Domain { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>Canonical name of the Windows (current identity) authentication mode.</summary>
    public const string WindowsMode = "Windows";

    /// <summary>Canonical name of the explicit username/password authentication mode.</summary>
    public const string UsernamePasswordMode = "UsernamePassword";

    /// <summary>True when the configured mode is Windows (current identity), case-insensitive.</summary>
    public bool UsesWindowsAuthentication => IsMode(WindowsMode);

    /// <summary>True when the configured mode is explicit username/password, case-insensitive.</summary>
    public bool UsesUsernamePasswordAuthentication => IsMode(UsernamePasswordMode);

    private bool IsMode(string mode)
        => string.Equals(Authentication?.Trim(), mode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates the explicit authentication configuration. Returns null when the
    /// configuration is usable, or a secret-free error message describing exactly
    /// what is missing. "Windows" never requires credentials; "UsernamePassword"
    /// always requires both a username and a password.
    /// </summary>
    /// <param name="options">The candidate configuration to validate.</param>
    /// <param name="passwordProvided">
    /// Pass false when the caller already resolved the password from the per-user credential
    /// store and only wants the remaining fields validated (used by the Settings save path,
    /// where a blank password input means "keep the stored one").
    /// </param>
    public static string? GetConfigurationError(ExchangeOptions options, bool passwordProvided = true)
    {
        if (!string.Equals(options.Authentication?.Trim(), WindowsMode, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Authentication?.Trim(), UsernamePasswordMode, StringComparison.OrdinalIgnoreCase))
        {
            return $"Exchange:Authentication '{options.Authentication}' is not supported. " +
                   $"Expected '{WindowsMode}' or '{UsernamePasswordMode}' (set it in Settings, or via " +
                   $"the EXCHANGE_AUTHENTICATION environment variable).";
        }

        if (options.UsesUsernamePasswordAuthentication)
        {
            if (string.IsNullOrWhiteSpace(options.Username))
            {
                return $"Exchange:Username is required when Exchange:Authentication is " +
                       $"{UsernamePasswordMode} (set it in Settings, or via the EXCHANGE_USERNAME " +
                       $"environment variable).";
            }

            if (passwordProvided && string.IsNullOrWhiteSpace(options.Password))
            {
                return $"Exchange:Password is required when Exchange:Authentication is " +
                       $"{UsernamePasswordMode} (set it in Settings, or via the EXCHANGE_PASSWORD " +
                       $"environment variable).";
            }
        }

        return null;
    }

    /// <summary>Optional mailbox (SMTP) to access via EWS impersonation.</summary>
    public string? Mailbox { get; set; }

    /// <summary>ExchangeVersion enum name; default Exchange2013_SP1.</summary>
    public string ExchangeVersion { get; set; } = "Exchange2013_SP1";

    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// True when <paramref name="ewsUrl"/> still points at one of the reserved
    /// documentation hosts (RFC 2606 example.* domains). The checked-in
    /// appsettings.json keeps such a value only as a public example; it is never a
    /// real endpoint, so the runtime refuses to treat it as one and reports a
    /// configuration error instead of failing on the network later.
    /// </summary>
    public static bool IsSamplePlaceholder(string? ewsUrl)
    {
        if (string.IsNullOrWhiteSpace(ewsUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(ewsUrl.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host == "example.com"
            || host == "mail.example.com"
            || host == "example.org"
            || host == "example.net"
            || host.EndsWith(".example.com", StringComparison.Ordinal)
            || host.EndsWith(".example.org", StringComparison.Ordinal)
            || host.EndsWith(".example.net", StringComparison.Ordinal);
    }
}

/// <summary>Result of the /health/exchange connectivity probe.</summary>
public sealed record ExchangeHealthStatus(bool IsHealthy, long? LatencyMs, string? Error);
