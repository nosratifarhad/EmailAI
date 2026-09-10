using EmailAI.Application.Exceptions;
using EmailAI.Domain.Exchange;
using Microsoft.Exchange.WebServices.Data;
using Microsoft.Extensions.Logging;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// The explicit authentication mode used for a single Exchange operation. There are
/// exactly two independent modes and never a runtime switch between them.
/// </summary>
internal enum ExchangeAuthMode
{
    /// <summary>
    /// Windows Integrated Authentication through the current Windows/process identity
    /// (<see cref="ExchangeService.UseDefaultCredentials"/>). Never combined with
    /// username/password credentials and never retried with them on failure.
    /// </summary>
    Windows,

    /// <summary>
    /// Explicit credentials (WebCredentials) from the configured username/password
    /// (the per-user credential store in Settings, or the deployment's
    /// EXCHANGE_DOMAIN / EXCHANGE_USERNAME / EXCHANGE_PASSWORD). The Windows identity is not
    /// used and is never attempted as a fallback.
    /// </summary>
    UsernamePassword,
}

/// <summary>The single configured authentication attempt with a log-safe display name.</summary>
internal sealed record ExchangeAuthAttempt(ExchangeAuthMode Mode, string DisplayName);

/// <summary>
/// Runs exactly one Exchange operation through the single explicitly configured
/// authentication mode (Windows or UsernamePassword).
/// <para>
/// Despite the historical "retry" naming of this seam there is NO retry and NO fallback: the
/// runner executes the one configured strategy exactly once. A failed authentication attempt
/// (HTTP 401/403 or any other non-success) is classified and reported as a clear
/// authentication/typed failure - the operation is never silently retried with the other
/// mode's credentials, and no NTLM or other mechanism is ever tried instead.
/// </para>
/// <para>
/// Connectivity, timeout, mailbox and other non-authentication failures keep their
/// own classification (<see cref="EwsErrorClassifier"/>); nothing here retries them
/// with a different credential mechanism either.
/// </para>
/// <para>
/// Passwords, Authorization headers and other secrets are never logged; log entries
/// carry only the attempt display name and the classified failure kind.
/// </para>
/// </summary>
internal static class ExchangeAuthRunner
{
    /// <summary>
    /// Builds the single authentication attempt for the configured mode. Invalid modes
    /// are rejected as configuration errors before any Exchange call is made.
    /// </summary>
    public static IReadOnlyList<ExchangeAuthAttempt> BuildAttempts(ExchangeOptions options)
    {
        if (options.UsesWindowsAuthentication)
        {
            return [new ExchangeAuthAttempt(ExchangeAuthMode.Windows, "Windows")];
        }

        if (options.UsesUsernamePasswordAuthentication)
        {
            return [new ExchangeAuthAttempt(ExchangeAuthMode.UsernamePassword, "Username + Password")];
        }

        throw new ExchangeMailException(
            ExchangeMailErrorKind.Configuration,
            $"Exchange:Authentication '{options.Authentication}' is not supported. " +
            $"Expected '{ExchangeOptions.WindowsMode}' or '{ExchangeOptions.UsernamePasswordMode}'.");
    }

    /// <summary>
    /// Executes <paramref name="action"/> once using the single configured
    /// authentication mode. A successful attempt returns its result. A failure is
    /// classified through <see cref="EwsErrorClassifier"/> and rethrown as the typed
    /// <see cref="ExchangeMailException"/>; no other credential mechanism is ever
    /// tried. Operation cancellations and configuration errors are never retried.
    /// </summary>
    public static async Task<TResult> ExecuteAsync<TResult>(
        ExchangeOptions options,
        string operation,
        Func<ExchangeService, CancellationToken, Task<TResult>> action,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var attempt = ResolveAttempt(options);
        cancellationToken.ThrowIfCancellationRequested();

        ExchangeService service;
        try
        {
            service = ExchangeServiceFactory.Create(options, attempt.Mode);
        }
        catch (ExchangeMailException configurationError)
        {
            logger.LogError(configurationError,
                "Exchange operation '{Operation}' failed: Exchange is not configured correctly.",
                operation);
            throw;
        }

        logger.LogInformation(
            "Exchange operation '{Operation}' authentication attempt: {AuthenticationMode}",
            operation, attempt.DisplayName);

        try
        {
            var result = await action(service, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Exchange operation '{Operation}' succeeded using {AuthenticationMode}.",
                operation, attempt.DisplayName);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var typed = exception as ExchangeMailException
                ?? EwsErrorClassifier.Categorize(exception, operation);

            // Configuration failures are not authentication attempts that could be
            // retried with another mechanism - rethrow immediately.
            if (typed.Kind == ExchangeMailErrorKind.Configuration)
            {
                throw;
            }

            logger.LogWarning(
                "Exchange operation '{Operation}' failed using {AuthenticationMode}: kind={Kind}. " +
                "No alternate authentication mode is attempted - the configured mode is used exactly once.",
                operation, attempt.DisplayName, typed.Kind);

            throw typed;
        }
    }

    private static ExchangeAuthAttempt ResolveAttempt(ExchangeOptions options)
    {
        var attempts = BuildAttempts(options);
        if (attempts.Count != 1)
        {
            // Defensive: every supported mode yields exactly one attempt.
            throw new ExchangeMailException(
                ExchangeMailErrorKind.Configuration,
                $"Exchange operation could not start: authentication plan must contain exactly " +
                $"one attempt but contained {attempts.Count}.");
        }

        return attempts[0];
    }
}
