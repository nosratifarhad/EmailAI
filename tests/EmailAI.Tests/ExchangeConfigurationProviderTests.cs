using EmailAI.Application.Exceptions;
using EmailAI.Application.Settings;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Settings;
using EmailAI.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailAI.Tests;

/// <summary>
/// The effective Exchange configuration policy: a complete per-user configuration wins as a
/// whole, otherwise the deployment configuration is used unchanged. Credentials from the two
/// sources are never mixed and the per-user password is only ever read from the credential
/// store (target EmailAI/Exchange).
/// </summary>
public sealed class ExchangeConfigurationProviderTests
{
    private const string UserUrl = "https://mail.contoso.com/EWS/Exchange.asmx";
    private const string EnvUrl = "https://deploy.contoso.com/EWS/Exchange.asmx";
    private const string EnvPassword = "env-pw";
    private const string UserPassword = "user-pw";

    private static ExchangeOptions Deployment(
        string authentication = ExchangeOptions.WindowsMode,
        string? username = null,
        string? password = null)
        => new()
        {
            EwsUrl = EnvUrl,
            Authentication = authentication,
            Username = username,
            Password = password,
            Mailbox = "delegate@contoso.com",
            ExchangeVersion = "Exchange2013_SP1",
            TimeoutSeconds = 33,
        };

    private static ExchangeConfigurationProvider CreateProvider(
        InMemoryUserSettingsStore settings,
        InMemorySecretStore secrets,
        ExchangeOptions environment)
        => new(
            settings,
            secrets,
            new StaticOptionsMonitor<ExchangeOptions>(environment),
            NullLogger<ExchangeConfigurationProvider>.Instance);

    [Fact]
    public async Task WithoutPerUserConfiguration_TheDeploymentSnapshotIsUsedUnchanged()
    {
        var deployment = Deployment(ExchangeOptions.UsernamePasswordMode, "deploy-bot", EnvPassword);
        var provider = CreateProvider(new InMemoryUserSettingsStore(), new InMemorySecretStore(), deployment);

        var effective = await provider.GetEffectiveOptionsAsync();

        Assert.Same(deployment, effective);
    }

    [Fact]
    public async Task WithAPerUserSectionWithoutEndpoint_TheDeploymentSnapshotIsUsed()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(new ExchangeUserSettings("  ", ExchangeOptions.WindowsMode, null, null), null));
        var deployment = Deployment();
        var provider = CreateProvider(settings, new InMemorySecretStore(), deployment);

        var effective = await provider.GetEffectiveOptionsAsync();

        Assert.Same(deployment, effective);
    }

    [Fact]
    public async Task PerUserWindowsMode_UsesTheUserEndpointAndNeverADeploymentPassword()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            new ExchangeUserSettings(UserUrl, ExchangeOptions.WindowsMode, "ignored-bot", "IGNORED"),
            null));
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretTargets.ExchangePassword, UserPassword);

        // The deployment would demand an explicit account - the per-user configuration wins
        // as a whole, so that account (and its password) is not used at all.
        var provider = CreateProvider(
            settings,
            secrets,
            Deployment(ExchangeOptions.UsernamePasswordMode, "deploy-bot", EnvPassword));

        var effective = await provider.GetEffectiveOptionsAsync();

        Assert.Equal(UserUrl, effective.EwsUrl);
        Assert.Equal(ExchangeOptions.WindowsMode, effective.Authentication);
        Assert.Null(effective.Username);
        Assert.Null(effective.Domain);
        Assert.Null(effective.Password);
        // Deployment-owned knobs are preserved.
        Assert.Equal("delegate@contoso.com", effective.Mailbox);
        Assert.Equal("Exchange2013_SP1", effective.ExchangeVersion);
        Assert.Equal(33, effective.TimeoutSeconds);
    }

    [Fact]
    public async Task PerUserUsernamePasswordMode_ReadsThePasswordFromTheCredentialStoreOnly()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            new ExchangeUserSettings(UserUrl, ExchangeOptions.UsernamePasswordMode, "mail-bot", "CONTOSO"),
            null));
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretTargets.ExchangePassword, UserPassword);

        var provider = CreateProvider(
            settings,
            secrets,
            Deployment(ExchangeOptions.UsernamePasswordMode, "deploy-bot", EnvPassword));

        var effective = await provider.GetEffectiveOptionsAsync();

        Assert.Equal(UserUrl, effective.EwsUrl);
        Assert.Equal("mail-bot", effective.Username);
        Assert.Equal("CONTOSO", effective.Domain);
        Assert.Equal(UserPassword, effective.Password);
        // The deployment password is never mixed in and no other secret target is touched.
        Assert.NotEqual(EnvPassword, effective.Password);
        Assert.Equal(new[] { SecretTargets.ExchangePassword }, secrets.Targets);
        Assert.Null(ExchangeOptions.GetConfigurationError(effective));
    }

    [Fact]
    public async Task PerUserUsernamePasswordMode_DoesNotBorrowTheDeploymentAccount()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            new ExchangeUserSettings(UserUrl, ExchangeOptions.UsernamePasswordMode, null, null),
            null));
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretTargets.ExchangePassword, UserPassword);

        var provider = CreateProvider(
            settings,
            secrets,
            Deployment(ExchangeOptions.UsernamePasswordMode, "deploy-bot", EnvPassword));

        var effective = await provider.GetEffectiveOptionsAsync();

        Assert.Null(effective.Username);
        Assert.Null(effective.Domain);
        // The incomplete per-user configuration is reported instead of silently using the
        // deployment account.
        var error = ExchangeOptions.GetConfigurationError(effective);
        Assert.NotNull(error);
        Assert.Contains("EXCHANGE_USERNAME", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("windows", ExchangeOptions.WindowsMode)]
    [InlineData("WINDOWS", ExchangeOptions.WindowsMode)]
    [InlineData("usernamepassword", ExchangeOptions.UsernamePasswordMode)]
    [InlineData("USERNAMEPASSWORD", ExchangeOptions.UsernamePasswordMode)]
    public async Task TheStoredModeSpellingIsCanonicalized(string stored, string expected)
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            new ExchangeUserSettings(UserUrl, stored, "mail-bot", null),
            null));
        var provider = CreateProvider(settings, new InMemorySecretStore(), Deployment());

        var effective = await provider.GetEffectiveOptionsAsync();

        Assert.Equal(expected, effective.Authentication);
    }

    [Fact]
    public async Task AnUnknownStoredModeIsPassedThroughSoValidationCanReportIt()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            new ExchangeUserSettings(UserUrl, "NTLM", null, null),
            null));
        var provider = CreateProvider(settings, new InMemorySecretStore(), Deployment());

        var effective = await provider.GetEffectiveOptionsAsync();

        Assert.Equal("NTLM", effective.Authentication);
        var error = ExchangeOptions.GetConfigurationError(effective);
        Assert.NotNull(error);
        Assert.Contains("Authentication", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheCredentialStoreFails_ATypedConfigurationErrorIsThrown()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            new ExchangeUserSettings(UserUrl, ExchangeOptions.UsernamePasswordMode, "mail-bot", null),
            null));
        var provider = CreateProvider(settings, new InMemorySecretStore(fail: true), Deployment());

        var failure = await Assert.ThrowsAsync<ExchangeMailException>(() =>
            provider.GetEffectiveOptionsAsync());

        Assert.Equal(ExchangeMailErrorKind.Configuration, failure.Kind);
        Assert.Contains("Credential Manager", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UserPassword, failure.Message, StringComparison.Ordinal);
    }
}
