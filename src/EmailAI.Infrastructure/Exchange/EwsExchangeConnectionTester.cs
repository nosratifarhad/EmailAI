using System.Diagnostics;
using EmailAI.Application.Exceptions;
using EmailAI.Application.Settings;
using EmailAI.Domain.Exchange;
using Microsoft.Extensions.Logging;
using Ews = Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// <see cref="IExchangeConnectionTester"/> implemented over EWS. It runs one real
/// root-folder probe through <see cref="ExchangeAuthRunner"/>, which authenticates with
/// exactly the single configured mode (Windows or UsernamePassword) and never falls back to
/// another credential mechanism.
///
/// Nothing is persisted here and never is: the tester only reads the snapshot it is given.
/// Errors are secret-free (no credentials, no Authorization headers).
/// </summary>
public sealed class EwsExchangeConnectionTester(ILogger<EwsExchangeConnectionTester> logger)
    : IExchangeConnectionTester
{
    public async Task<ExchangeHealthStatus> TestAsync(
        ExchangeOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await ExchangeAuthRunner.ExecuteAsync(
                options,
                "SettingsTestConnection",
                async (service, token) =>
                {
                    await service.FindFolders(
                        Ews.WellKnownFolderName.Root,
                        new Ews.FolderView(1),
                        token);
                    return true;
                },
                logger,
                cancellationToken);

            stopwatch.Stop();
            logger.LogInformation(
                "Exchange connection test succeeded in {LatencyMs} ms.", stopwatch.ElapsedMilliseconds);

            return new ExchangeHealthStatus(true, stopwatch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new ExchangeHealthStatus(false, stopwatch.ElapsedMilliseconds, "The connection test was cancelled.");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var typed = exception as ExchangeMailException
                ?? EwsErrorClassifier.Categorize(exception, "SettingsTestConnection");

            var message = typed.Kind == ExchangeMailErrorKind.Authentication
                ? AuthenticationFailureText(options)
                : EwsErrorClassifier.SafeText(typed.Kind);

            logger.LogWarning(
                "Exchange connection test failed. kind={Kind}", typed.Kind);

            return new ExchangeHealthStatus(false, stopwatch.ElapsedMilliseconds, message);
        }
    }

    /// <summary>
    /// Mode-specific, secret-free explanation so the Settings page says WHY the probe failed
    /// and restates that only the configured mode is ever attempted.
    /// </summary>
    private static string AuthenticationFailureText(ExchangeOptions options)
        => options.UsesUsernamePasswordAuthentication
            ? "Exchange authentication failed: the configured username or password was rejected " +
              "by the Exchange server. Verify the account (and the domain when needed) in Settings. " +
              "The Windows identity is not used in UsernamePassword mode and there is no automatic " +
              "fallback to Windows authentication."
            : "Exchange authentication failed: the current Windows account was rejected by the " +
              "Exchange server. EmailAI never falls back to a username/password or NTLM credential " +
              "mechanism; check that this Windows account is allowed, or switch to UsernamePassword " +
              "mode with an explicit account.";
}
