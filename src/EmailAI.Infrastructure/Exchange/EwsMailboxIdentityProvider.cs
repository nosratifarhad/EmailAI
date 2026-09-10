using EmailAI.Application.Exchange;
using EmailAI.Application.Exceptions;
using EmailAI.Application.Settings;
using EmailAI.Domain.Exchange;
using Microsoft.Extensions.Logging;
using Ews = Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// Resolves the current user from the Exchange context EmailAI already authenticates with.
///
/// Resolution order for the identity CANDIDATE (what gets looked up):
///   1. the mailbox configured for EWS impersonation (<c>Exchange:Mailbox</c>) - that is the
///      mailbox whose mail is being read, even when the authenticating account is a service
///      account, so it is the most accurate answer;
///   2. the account configured for UsernamePassword authentication (the mailbox owner);
///   3. the Windows/process account (Windows authentication).
///
/// The candidate is then resolved through the Exchange DIRECTORY (<c>ResolveNames</c>), which
/// is what makes the returned display name and SMTP address authoritative (AD data) instead
/// of a guess taken from an email. When Exchange cannot answer - not configured, unreachable
/// or rejecting the account - the locally known values are returned instead, so mail and AI
/// keep working; the identity is then simply flagged as less authoritative
/// (<see cref="MailboxIdentity.Source"/>).
///
/// Caching: the resolved identity is cached per configuration fingerprint (endpoint, mode,
/// account, mailbox, Windows account) for a bounded time, so a settings change is picked up
/// without a restart and an AI request does not pay for a directory lookup every time. The
/// cache key never contains a secret (no password), and nothing secret is ever logged.
/// </summary>
public sealed class EwsMailboxIdentityProvider(
    IExchangeConfigurationProvider configuration,
    IExchangeIdentityProvider windowsIdentity,
    ILogger<EwsMailboxIdentityProvider> logger) : IMailboxIdentityProvider
{
    private const string Operation = "ResolveCurrentUser";

    /// <summary>How long a successfully resolved directory identity is reused.</summary>
    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long a failed lookup is remembered (keeps an unreachable server cheap).</summary>
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private string? _cacheKey;
    private MailboxIdentity? _cached;
    private DateTimeOffset _cachedUntil;

    public async Task<MailboxIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        ExchangeOptions? options = null;
        try
        {
            options = await configuration.GetEffectiveOptionsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A configuration/credential problem must never break the mail or AI surface:
            // the identity simply stays unknown.
            logger.LogDebug(
                "Current-user identity fell back to the local account: Exchange configuration is unavailable. reason={Reason}",
                exception.GetType().Name);
        }

        var fallback = LocalIdentity(options);
        var cacheKey = CacheKey(options, fallback);

        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        MailboxIdentity identity;
        if (!CanQueryDirectory(options, out var candidate))
        {
            identity = fallback;
            Store(cacheKey, identity, SuccessLifetime);
            return identity;
        }

        try
        {
            var resolved = await ExchangeAuthRunner.ExecuteAsync(
                options!,
                Operation,
                async (service, token) => await ResolveAsync(service, candidate!, token),
                logger,
                cancellationToken);

            identity = resolved ?? fallback;
            Store(cacheKey, identity, SuccessLifetime);
            logger.LogInformation(
                "Current-user identity resolved: {Identity}", identity.Describe());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never throw: an unresolvable identity is a degraded (but working) state.
            logger.LogDebug(
                "Current-user identity could not be resolved through Exchange ({Reason}); using the configured account.",
                exception is ExchangeMailException mail ? mail.Kind.ToString() : exception.GetType().Name);
            identity = fallback;
            Store(cacheKey, identity, FailureLifetime);
        }

        return identity;
    }

    /// <summary>
    /// The best identity that is known without asking Exchange: the impersonated mailbox,
    /// the configured account, or the Windows account. Each keeps its own source so the UI can
    /// say how authoritative the answer is.
    /// </summary>
    private MailboxIdentity LocalIdentity(ExchangeOptions? options)
    {
        if (options is not null)
        {
            if (!string.IsNullOrWhiteSpace(options.Mailbox))
            {
                var mailbox = options.Mailbox.Trim();
                return MailboxIdentity.Create(
                    SmtpDisplayName(mailbox),
                    LooksLikeSmtpAddress(mailbox) ? mailbox : null,
                    mailbox,
                    MailboxIdentity.ConfiguredMailboxSource);
            }

            if (options.UsesUsernamePasswordAuthentication && !string.IsNullOrWhiteSpace(options.Username))
            {
                var username = options.Username.Trim();
                var account = string.IsNullOrWhiteSpace(options.Domain)
                    ? username
                    : $"{options.Domain.Trim()}\\{username}";

                var smtp = LooksLikeSmtpAddress(username) ? username : null;

                return MailboxIdentity.Create(
                    smtp is null ? null : SmtpDisplayName(smtp),
                    smtp,
                    account,
                    MailboxIdentity.ConfiguredAccountSource);
            }
        }

        var windows = windowsIdentity.GetCurrentIdentity();
        return MailboxIdentity.Create(
            null,
            null,
            windows.FullName ?? windows.UserName,
            MailboxIdentity.WindowsIdentitySource);
    }

    /// <summary>
    /// The value handed to the Exchange directory lookup. An SMTP address or UPN resolves to
    /// the mailbox itself; a <c>DOMAIN\user</c> account resolves through the AD account name.
    /// </summary>
    private static string? ResolveCandidate(ExchangeOptions options, MailboxIdentity local)
    {
        if (!string.IsNullOrWhiteSpace(options.Mailbox))
        {
            return options.Mailbox.Trim();
        }

        if (options.UsesUsernamePasswordAuthentication && !string.IsNullOrWhiteSpace(options.Username))
        {
            var username = options.Username.Trim();
            if (LooksLikeSmtpAddress(username) || string.IsNullOrWhiteSpace(options.Domain))
            {
                return username;
            }

            return $"{options.Domain.Trim()}\\{username}";
        }

        // Windows mode: look the process account up by name.
        return local.AccountName;
    }

    /// <summary>
    /// True when a directory lookup is worth a network round trip: Exchange must be configured
    /// with a usable endpoint and there must be something to look up.
    /// </summary>
    private static bool CanQueryDirectory(ExchangeOptions? options, out string? candidate)
    {
        candidate = null;
        if (options is null
            || string.IsNullOrWhiteSpace(options.EwsUrl)
            || ExchangeOptions.IsSamplePlaceholder(options.EwsUrl)
            || ExchangeOptions.GetConfigurationError(options) is not null)
        {
            return false;
        }

        candidate = ResolveCandidate(options, LocalIdentityForLookup(options));
        return !string.IsNullOrWhiteSpace(candidate);
    }

    /// <summary>
    /// Identity used only to decide the lookup candidate (avoids re-reading the Windows token
    /// when the configuration already names an account).
    /// </summary>
    private static MailboxIdentity LocalIdentityForLookup(ExchangeOptions options)
        => MailboxIdentity.Create(
            null,
            null,
            options.UsesUsernamePasswordAuthentication ? options.Username : null,
            options.UsesUsernamePasswordAuthentication
                ? MailboxIdentity.ConfiguredAccountSource
                : MailboxIdentity.WindowsIdentitySource);

    /// <summary>
    /// Directory lookup through EWS <c>ResolveNames</c>: the authoritative display name and
    /// SMTP address of the mailbox account. Returns null when the directory returns nothing
    /// usable, so the caller can fall back to the configured values.
    /// </summary>
    private static async Task<MailboxIdentity?> ResolveAsync(
        Ews.ExchangeService service,
        string candidate,
        CancellationToken cancellationToken)
    {
        var resolutions = await service.ResolveName(
            candidate,
            Ews.ResolveNameSearchLocation.DirectoryOnly,
            returnContactDetails: true,
            new Ews.PropertySet(Ews.BasePropertySet.FirstClassProperties),
            cancellationToken);

        Ews.NameResolution? best = null;
        for (var index = 0; index < resolutions.Count; index++)
        {
            var resolution = resolutions[index];
            if (string.IsNullOrWhiteSpace(resolution.Mailbox?.Address))
            {
                continue;
            }

            if (string.Equals(resolution.Mailbox.Address, candidate, StringComparison.OrdinalIgnoreCase))
            {
                best = resolution;
                break;
            }

            best ??= resolution;
        }

        if (best?.Mailbox is not { } mailbox)
        {
            return null;
        }

        var displayName = string.IsNullOrWhiteSpace(mailbox.Name)
            ? best.Contact?.DisplayName
            : mailbox.Name;

        return MailboxIdentity.Create(
            displayName,
            mailbox.Address,
            candidate,
            MailboxIdentity.ExchangeDirectorySource);
    }

    private bool TryGetCached(string cacheKey, out MailboxIdentity identity)
    {
        lock (_gate)
        {
            if (_cached is not null
                && string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal)
                && DateTimeOffset.UtcNow < _cachedUntil)
            {
                identity = _cached;
                return true;
            }
        }

        identity = MailboxIdentity.None;
        return false;
    }

    private void Store(string cacheKey, MailboxIdentity identity, TimeSpan lifetime)
    {
        lock (_gate)
        {
            _cacheKey = cacheKey;
            _cached = identity;
            _cachedUntil = DateTimeOffset.UtcNow + lifetime;
        }
    }

    /// <summary>
    /// Cache fingerprint: every input that changes WHICH mailbox is read. Deliberately
    /// excludes the password (never part of a cache key) so a rotated password does not
    /// invalidate a correct identity, while changing the account/endpoint/mode does.
    /// </summary>
    private string CacheKey(ExchangeOptions? options, MailboxIdentity local)
        => string.Join(
            '|',
            options?.EwsUrl?.Trim() ?? string.Empty,
            options?.Authentication?.Trim() ?? string.Empty,
            options?.Mailbox?.Trim() ?? string.Empty,
            options?.Username?.Trim() ?? string.Empty,
            options?.Domain?.Trim() ?? string.Empty,
            local.AccountName ?? string.Empty);

    private static bool LooksLikeSmtpAddress(string value)
        => value.Contains('@', StringComparison.Ordinal);

    /// <summary>A readable provisional name from an SMTP address ("farhad.nosrati" =&gt; "farhad nosrati").</summary>
    private static string? SmtpDisplayName(string smtpAddress)
    {
        var at = smtpAddress.IndexOf('@');
        if (at <= 0)
        {
            return null;
        }

        var local = smtpAddress[..at].Replace('.', ' ').Replace('_', ' ').Trim();
        return local.Length == 0 ? null : local;
    }
}
