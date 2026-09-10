using EmailAI.Application.Exceptions;
using EmailAI.Domain.Exchange;
using EmailAI.Infrastructure.Exchange;
using Microsoft.Extensions.Logging.Abstractions;
using Ews = Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Tests;

/// <summary>
/// Runtime Exchange authentication tests (network-free) for the two explicit modes.
/// The EWS service objects are real (built by ExchangeServiceFactory) but the
/// actions/probes never perform a network round-trip, so mode wiring and the
/// ABSENCE of any fallback between Windows and UsernamePassword authentication are
/// deterministic.
/// </summary>
public sealed class ExchangeAuthModeTests
{
    private const string WindowsModeName = "Windows";
    private const string UsernamePasswordModeName = "Username + Password";

    private static ExchangeOptions Options(string authentication, bool withCredentials)
        => new()
        {
            EwsUrl = "https://exch.example.test/EWS/Exchange.asmx",
            Authentication = authentication,
            Domain = withCredentials ? "EXAMPLE" : null,
            Username = withCredentials ? "mail-bot" : null,
            Password = withCredentials ? "secret-pw" : null,
            ExchangeVersion = "Exchange2013_SP1",
            TimeoutSeconds = 5,
        };

    private static string ModeOf(Ews.ExchangeService service)
        => service.UseDefaultCredentials ? WindowsModeName : UsernamePasswordModeName;

    // ------------------------------------------------------------------
    // Attempt planning
    // ------------------------------------------------------------------

    [Fact]
    public void BuildAttempts_Windows_YieldsSingleWindowsAttempt_EvenWhenExplicitCredentialsConfigured()
    {
        // Explicit credentials exist in the options but must NEVER be appended as a
        // fallback attempt.
        var attempts = ExchangeAuthRunner.BuildAttempts(Options("Windows", withCredentials: true));

        var attempt = Assert.Single(attempts);
        Assert.Equal(ExchangeAuthMode.Windows, attempt.Mode);
        Assert.Equal(WindowsModeName, attempt.DisplayName);
    }

    [Fact]
    public void BuildAttempts_UsernamePassword_YieldsSingleUsernamePasswordAttempt()
    {
        var attempts = ExchangeAuthRunner.BuildAttempts(Options("UsernamePassword", withCredentials: true));

        var attempt = Assert.Single(attempts);
        Assert.Equal(ExchangeAuthMode.UsernamePassword, attempt.Mode);
        Assert.Equal(UsernamePasswordModeName, attempt.DisplayName);
    }

    // ------------------------------------------------------------------
    // Windows mode
    // ------------------------------------------------------------------

    [Fact]
    public async Task WindowsMode_UsesWindowsCredentials_NotExplicitUsernamePassword()
    {
        var options = Options("Windows", withCredentials: true); // credentials present but ignored
        var executed = new List<string>();

        var result = await ExchangeAuthRunner.ExecuteAsync(
            options,
            "GetMessages",
            (service, _) =>
            {
                executed.Add(ModeOf(service));
                Assert.True(service.UseDefaultCredentials);
                Assert.Null(service.Credentials);
                return Task.FromResult("ok");
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.Equal(new[] { WindowsModeName }, executed);
    }

    [Fact]
    public async Task WindowsMode_AuthenticationFailure_ReturnsAuthenticationError_AndNeverFallsBack()
    {
        var options = Options("Windows", withCredentials: true); // creds must NOT be tried
        var executed = new List<string>();

        var failure = await Assert.ThrowsAsync<ExchangeMailException>(() =>
            ExchangeAuthRunner.ExecuteAsync<bool>(
                options,
                "GetMessages",
                (service, _) =>
                {
                    executed.Add(ModeOf(service));
                    throw new InvalidOperationException(
                        "The request failed with HTTP status code 401 Unauthorized.");
                },
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(ExchangeMailErrorKind.Authentication, failure.Kind);
        Assert.Equal(new[] { WindowsModeName }, executed);
        Assert.DoesNotContain("secret-pw", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-bot", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsMode_ConnectivityFailure_ReportsConnectivity_WithoutCredentialRetry()
    {
        var options = Options("Windows", withCredentials: true);
        var executed = new List<string>();

        var failure = await Assert.ThrowsAsync<ExchangeMailException>(() =>
            ExchangeAuthRunner.ExecuteAsync<bool>(
                options,
                "GetMessages",
                (service, _) =>
                {
                    executed.Add(ModeOf(service));
                    throw new HttpRequestException("Connection refused.");
                },
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(ExchangeMailErrorKind.Connectivity, failure.Kind);
        Assert.Equal(new[] { WindowsModeName }, executed);
    }

    [Fact]
    public async Task WindowsMode_TimeoutFailure_ReportsTimeout_WithoutCredentialRetry()
    {
        var options = Options("Windows", withCredentials: true);
        var executed = new List<string>();

        var failure = await Assert.ThrowsAsync<ExchangeMailException>(() =>
            ExchangeAuthRunner.ExecuteAsync<bool>(
                options,
                "GetMessages",
                (service, _) =>
                {
                    executed.Add(ModeOf(service));
                    throw new InvalidOperationException("The operation has timed out.");
                },
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(ExchangeMailErrorKind.Timeout, failure.Kind);
        Assert.Equal(new[] { WindowsModeName }, executed);
    }

    [Fact]
    public async Task WindowsMode_HealthProbe401_ErrorExplainsNoFallback_AndNeverSecrets()
    {
        var options = Options("Windows", withCredentials: true);
        var mail = new EwsExchangeMailService(
            new StaticExchangeConfigurationProvider(options),
            NullLogger<EwsExchangeMailService>.Instance);

        var status = await mail.ProbeExchangeHealthAsync((service, _) =>
            throw new InvalidOperationException(
                "The request failed with HTTP status code 401 Unauthorized."));

        Assert.False(status.IsHealthy);
        Assert.NotNull(status.Error);
        Assert.Contains("never falls back", status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-pw", status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-bot", status.Error, StringComparison.Ordinal);
    }


    // ------------------------------------------------------------------
    // UsernamePassword mode
    // ------------------------------------------------------------------

    [Fact]
    public async Task UsernamePasswordMode_UsesSuppliedUsernameAndPassword_NotWindowsIdentity()
    {
        var options = Options("UsernamePassword", withCredentials: true);
        Ews.ExchangeService? captured = null;

        var result = await ExchangeAuthRunner.ExecuteAsync(
            options,
            "GetMessages",
            (service, _) =>
            {
                captured = service;
                Assert.False(service.UseDefaultCredentials);
                return Task.FromResult("ok");
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.NotNull(captured);
        var credentials = Assert.IsType<Ews.WebCredentials>(captured.Credentials);
        var account = Assert.IsAssignableFrom<System.Net.NetworkCredential>(credentials.Credentials);
        Assert.Equal("EXAMPLE", account.Domain);
        Assert.Equal("mail-bot", account.UserName);
        Assert.Equal("secret-pw", account.Password);
    }

    [Fact]
    public async Task UsernamePasswordMode_AuthenticationFailure_ReturnsAuthenticationError_AndNeverFallsBackToWindows()
    {
        var options = Options("UsernamePassword", withCredentials: true);
        var executed = new List<string>();

        var failure = await Assert.ThrowsAsync<ExchangeMailException>(() =>
            ExchangeAuthRunner.ExecuteAsync<bool>(
                options,
                "GetMessages",
                (service, _) =>
                {
                    executed.Add(ModeOf(service));
                    throw new InvalidOperationException(
                        "The request failed with HTTP status code 401 Unauthorized.");
                },
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(ExchangeMailErrorKind.Authentication, failure.Kind);
        Assert.Equal(new[] { UsernamePasswordModeName }, executed); // Windows was never attempted
        Assert.DoesNotContain("secret-pw", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-bot", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsernamePasswordMode_MissingCredentials_FailsAsConfiguration_BeforeAnyNetworkAttempt()
    {
        // No username at all -> configuration error naming EXCHANGE_USERNAME.
        var executed = new List<string>();
        var noUsername = Options("UsernamePassword", withCredentials: false);

        var first = await Assert.ThrowsAsync<ExchangeMailException>(() =>
            ExchangeAuthRunner.ExecuteAsync<bool>(
                noUsername,
                "GetMessages",
                (service, _) =>
                {
                    executed.Add(ModeOf(service));
                    return Task.FromResult(true);
                },
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(ExchangeMailErrorKind.Configuration, first.Kind);
        Assert.Empty(executed);
        Assert.Contains("EXCHANGE_USERNAME", first.Message, StringComparison.Ordinal);

        // Username present but no password -> configuration error naming EXCHANGE_PASSWORD.
        var executed2 = new List<string>();
        var noPassword = new ExchangeOptions
        {
            EwsUrl = "https://exch.example.test/EWS/Exchange.asmx",
            Authentication = ExchangeOptions.UsernamePasswordMode,
            Username = "mail-bot",
            ExchangeVersion = "Exchange2013_SP1",
            TimeoutSeconds = 5,
        };

        var second = await Assert.ThrowsAsync<ExchangeMailException>(() =>
            ExchangeAuthRunner.ExecuteAsync<bool>(
                noPassword,
                "GetMessages",
                (service, _) =>
                {
                    executed2.Add(ModeOf(service));
                    return Task.FromResult(true);
                },
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Equal(ExchangeMailErrorKind.Configuration, second.Kind);
        Assert.Empty(executed2);
        Assert.Contains("EXCHANGE_PASSWORD", second.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-bot", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsernamePasswordMode_HealthProbe401_ErrorNeverContainsCredentials()
    {
        var options = Options("UsernamePassword", withCredentials: true);
        var mail = new EwsExchangeMailService(
            new StaticExchangeConfigurationProvider(options),
            NullLogger<EwsExchangeMailService>.Instance);

        var status = await mail.ProbeExchangeHealthAsync((service, _) =>
            throw new InvalidOperationException(
                "The request failed with HTTP status code 401 Unauthorized."));

        Assert.False(status.IsHealthy);
        Assert.NotNull(status.Error);
        Assert.Contains("rejected", status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-pw", status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-bot", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsernamePasswordMode_HealthProbe_DoesNotUseWindowsIdentity()
    {
        var options = Options("UsernamePassword", withCredentials: true);
        var mail = new EwsExchangeMailService(
            new StaticExchangeConfigurationProvider(options),
            NullLogger<EwsExchangeMailService>.Instance);

        var status = await mail.ProbeExchangeHealthAsync((service, _) =>
        {
            Assert.False(service.UseDefaultCredentials);
            Assert.NotNull(service.Credentials);
            return Task.CompletedTask;
        });

        Assert.True(status.IsHealthy);
    }
}

