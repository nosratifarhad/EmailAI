using EmailAI.Domain.Exchange;
using EmailAI.Infrastructure.Exchange;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailAI.Tests;

/// <summary>
/// Opt-in REAL Exchange integration tests. When the required variables below are
/// missing the tests report "not configured" and complete without touching any
/// Exchange server:
///   Windows mode        : EXCHANGE_EWS_URL (+ EXCHANGE_AUTHENTICATION=Windows)
///   UsernamePassword    : EXCHANGE_EWS_URL, EXCHANGE_USERNAME, EXCHANGE_PASSWORD
///                         (+ EXCHANGE_DOMAIN when needed)
/// Credentials come from the environment only and are never printed or committed.
/// Each configured test performs one real EWS root-folder probe through the mode.
/// </summary>
public sealed class ExchangeIntegrationTests
{
    [Fact]
    public async Task RealExchange_WindowsMode_ProbesThroughCurrentWindowsIdentity()
    {
        if (!IntegrationTestEnvironment.ExchangeConfigured(ExchangeOptions.WindowsMode))
        {
            // Not configured: report it and complete without touching any service.
            Console.WriteLine(
                "EMAILAI_EXCHANGE_INTEGRATION_TEST not configured (Windows mode) - Exchange " +
                "integration test does not run. Set EMAILAI_EXCHANGE_INTEGRATION_TEST=true and " +
                "EXCHANGE_EWS_URL (EXCHANGE_AUTHENTICATION=Windows) to run it against a real " +
                "Exchange server.");
            return;
        }

        var mail = CreateMailService(ExchangeOptions.WindowsMode);
        var status = await mail.CheckHealthAsync(CancellationToken.None);

        Assert.True(status.IsHealthy, status.Error ?? "Exchange health probe failed with no error.");
    }

    [Fact]
    public async Task RealExchange_UsernamePasswordMode_ProbesThroughConfiguredAccount()
    {
        if (!IntegrationTestEnvironment.ExchangeConfigured(ExchangeOptions.UsernamePasswordMode))
        {
            // Not configured: report it and complete without touching any service.
            Console.WriteLine(
                "EMAILAI_EXCHANGE_INTEGRATION_TEST not configured (UsernamePassword mode) - " +
                "Exchange integration test does not run. Set EMAILAI_EXCHANGE_INTEGRATION_TEST=true " +
                "and EXCHANGE_EWS_URL, EXCHANGE_USERNAME, EXCHANGE_PASSWORD " +
                "(EXCHANGE_AUTHENTICATION=UsernamePassword, EXCHANGE_DOMAIN optional) to run it " +
                "against a real Exchange server.");
            return;
        }

        var mail = CreateMailService(ExchangeOptions.UsernamePasswordMode);
        var status = await mail.CheckHealthAsync(CancellationToken.None);

        Assert.True(status.IsHealthy, status.Error ?? "Exchange health probe failed with no error.");
    }

    private static EwsExchangeMailService CreateMailService(string mode)
    {
        var options = IntegrationTestEnvironment.ExchangeOptionsFor(mode);
        return new EwsExchangeMailService(
            new StaticExchangeConfigurationProvider(options),
            NullLogger<EwsExchangeMailService>.Instance);
    }
}
