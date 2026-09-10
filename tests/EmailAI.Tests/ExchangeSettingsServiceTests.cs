using EmailAI.Application.Settings;
using EmailAI.Domain.AI;
using EmailAI.Domain.Exchange;
using EmailAI.Domain.Settings;
using EmailAI.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailAI.Tests;

/// <summary>
/// The per-user Exchange settings coordinator: validation before any write, strict separation
/// between the non-secret document and the credential store, no credential mixing with the
/// deployment configuration, and Windows mode never keeping a password.
/// </summary>
public sealed class ExchangeSettingsServiceTests
{
    private const string Url = "https://mail.contoso.com/EWS/Exchange.asmx";
    private const string EnvUrl = "https://deploy.contoso.com/EWS/Exchange.asmx";
    private const string Password = "s3cret-pw";

    private static ExchangeOptions EnvironmentOptions(
        string authentication = ExchangeOptions.WindowsMode,
        string? username = null,
        string? password = null,
        string ewsUrl = EnvUrl)
        => new()
        {
            EwsUrl = ewsUrl,
            Authentication = authentication,
            Username = username,
            Password = password,
            Mailbox = "mailbox@contoso.com",
            ExchangeVersion = "Exchange2013_SP1",
            TimeoutSeconds = 42,
        };

    private static ExchangeSettingsService CreateService(
        InMemoryUserSettingsStore settings,
        InMemorySecretStore secrets,
        ExchangeOptions? environment = null)
        => new(
            settings,
            secrets,
            new StaticExchangeConfigurationProvider(environment ?? EnvironmentOptions()),
            NullLogger<ExchangeSettingsService>.Instance);

    private static ExchangeSettingsService CreateService(
        out InMemoryUserSettingsStore settings,
        out InMemorySecretStore secrets,
        ExchangeOptions? environment = null)
    {
        settings = new InMemoryUserSettingsStore();
        secrets = new InMemorySecretStore(fail: false);
        return CreateService(settings, secrets, environment);
    }

    /// <summary>
    /// The service wired to the REAL effective-configuration provider, so the per-user
    /// precedence (and the password coming from the credential store) is exercised end to end.
    /// </summary>
    private static ExchangeSettingsService CreateServiceWithEffectiveConfiguration(
        InMemoryUserSettingsStore settings,
        InMemorySecretStore secrets,
        ExchangeOptions? environment = null)
        => new(
            settings,
            secrets,
            new ExchangeConfigurationProvider(
                settings,
                secrets,
                new StaticOptionsMonitor<ExchangeOptions>(environment ?? EnvironmentOptions()),
                NullLogger<ExchangeConfigurationProvider>.Instance),
            NullLogger<ExchangeSettingsService>.Instance);

    private static ExchangeUserSettings UserSettings(
        string authentication = ExchangeOptions.WindowsMode,
        string? username = null,
        string? domain = null,
        string ewsUrl = Url)
        => new(ewsUrl, authentication, username, domain);

    [Fact]
    public async Task GetStatus_WithoutPerUserConfiguration_ReportsTheDeploymentConfiguration()
    {
        var service = CreateService(out _, out _, EnvironmentOptions());

        var status = await service.GetStatusAsync();

        Assert.True(status.Configured);
        Assert.False(status.UserManaged);
        Assert.True(status.UsesDefaultCredentials);
        Assert.Equal(EnvUrl, status.EwsUrl);
        Assert.Equal(ExchangeOptions.WindowsMode, status.Authentication);
        Assert.Null(status.Username);
        Assert.Null(status.Domain);
        Assert.False(status.HasPassword);
    }

    [Fact]
    public async Task GetStatus_WithoutPerUserConfiguration_WhenTheEndpointIsTheSamplePlaceholder_ReportsNotConfigured()
    {
        var service = CreateService(out _, out _, EnvironmentOptions(ewsUrl: "https://mail.example.com/EWS/Exchange.asmx"));

        var status = await service.GetStatusAsync();

        Assert.False(status.Configured);
        Assert.False(status.UserManaged);
        Assert.Null(status.EwsUrl);
    }

    [Fact]
    public async Task GetStatus_WithoutPerUserConfiguration_UsernamePassword_ReportsPresenceOnly()
    {
        var service = CreateService(
            out _,
            out _,
            EnvironmentOptions(ExchangeOptions.UsernamePasswordMode, username: "deploy-bot", password: "env-pw"));

        var status = await service.GetStatusAsync();

        Assert.True(status.Configured);
        Assert.False(status.UserManaged);
        Assert.False(status.UsesDefaultCredentials);
        // The deployment's account is never surfaced - only that it exists.
        Assert.Null(status.Username);
        Assert.Null(status.Domain);
        Assert.True(status.HasPassword);
    }

    [Fact]
    public async Task GetStatus_UserManagedWindows_DoesNotSurfaceCredentials_AndNeverReadsTheSecretStore()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(UserSettings(), null));

        // A failing credential store proves Windows mode never touches a password.
        var secrets = new InMemorySecretStore(fail: true);
        var service = CreateService(settings, secrets, EnvironmentOptions(ExchangeOptions.UsernamePasswordMode, "deploy-bot", "env-pw"));

        var status = await service.GetStatusAsync();

        Assert.True(status.Configured);
        Assert.True(status.UserManaged);
        Assert.True(status.UsesDefaultCredentials);
        Assert.Equal(Url, status.EwsUrl);
        Assert.Equal(ExchangeOptions.WindowsMode, status.Authentication);
        Assert.Null(status.Username);
        Assert.Null(status.Domain);
        Assert.False(status.HasPassword);
    }

    [Fact]
    public async Task GetStatus_UserManagedUsernamePassword_ReportsTheAccountAndPasswordPresence()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            UserSettings(ExchangeOptions.UsernamePasswordMode, "mail-bot", "CONTOSO"),
            null));
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretTargets.ExchangePassword, Password);
        var service = CreateService(settings, secrets);

        var status = await service.GetStatusAsync();

        Assert.True(status.Configured);
        Assert.True(status.UserManaged);
        Assert.False(status.UsesDefaultCredentials);
        Assert.Equal(Url, status.EwsUrl);
        Assert.Equal("mail-bot", status.Username);
        Assert.Equal("CONTOSO", status.Domain);
        Assert.True(status.HasPassword);
    }

    // ------------------------------------------------------------------
    // Save: the non-secret document and the secret stay strictly separated
    // ------------------------------------------------------------------

    [Fact]
    public async Task Save_WindowsMode_StoresTheEndpointAndRemovesAnyStoredPassword()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            UserSettings(ExchangeOptions.UsernamePasswordMode, "mail-bot", "CONTOSO"),
            null));
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretTargets.ExchangePassword, Password);
        var service = CreateService(settings, secrets);

        await service.SaveAsync(new ExchangeSettingsUpdate(
            Url,
            ExchangeOptions.WindowsMode,
            Username: "ignored",
            Domain: "IGNORED",
            Password: null));

        var stored = settings.Stored.Exchange;
        Assert.NotNull(stored);
        Assert.Equal(ExchangeOptions.WindowsMode, stored!.Authentication);
        Assert.Null(stored.Username);
        Assert.Null(stored.Domain);
        // Windows mode never keeps a password: the previously stored one is gone.
        Assert.Null(await secrets.ReadAsync(SecretTargets.ExchangePassword));
        Assert.Empty(secrets.Targets);
    }

    [Fact]
    public async Task Save_UsernamePassword_StoresThePasswordOnlyInTheSecretStore()
    {
        var service = CreateService(out var settings, out var secrets);

        await service.SaveAsync(new ExchangeSettingsUpdate(
            Url,
            ExchangeOptions.UsernamePasswordMode,
            "mail-bot",
            "CONTOSO",
            Password));

        var stored = settings.Stored.Exchange;
        Assert.NotNull(stored);
        Assert.Equal(Url, stored!.EwsUrl);
        Assert.Equal(ExchangeOptions.UsernamePasswordMode, stored.Authentication);
        Assert.Equal("mail-bot", stored.Username);
        Assert.Equal("CONTOSO", stored.Domain);

        // The secret lives under its own target only.
        Assert.Equal(Password, await secrets.ReadAsync(SecretTargets.ExchangePassword));
        Assert.Equal(new[] { SecretTargets.ExchangePassword }, secrets.Targets);
    }

    [Fact]
    public async Task Save_UsernamePassword_WithABlankPassword_KeepsTheStoredPassword()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            UserSettings(ExchangeOptions.UsernamePasswordMode, "mail-bot", null),
            null));
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretTargets.ExchangePassword, Password);
        var service = CreateService(settings, secrets);

        await service.SaveAsync(new ExchangeSettingsUpdate(
            Url,
            ExchangeOptions.UsernamePasswordMode,
            "mail-bot",
            null,
            Password: "   "));

        Assert.Equal(Password, await secrets.ReadAsync(SecretTargets.ExchangePassword));
    }

    [Fact]
    public async Task Save_UsernamePassword_WithoutAnyPassword_IsRejectedWithoutSideEffects()
    {
        var service = CreateService(out var settings, out var secrets);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new ExchangeSettingsUpdate(Url, ExchangeOptions.UsernamePasswordMode, "mail-bot", null, null)));

        Assert.Contains("EXCHANGE_PASSWORD", failure.Message, StringComparison.Ordinal);
        Assert.Null(settings.Stored.Exchange);
        Assert.Empty(secrets.Targets);
    }

    [Fact]
    public async Task Save_UsernamePassword_WithoutAUsername_IsRejectedWithoutSideEffects()
    {
        var service = CreateService(out var settings, out var secrets);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new ExchangeSettingsUpdate(Url, ExchangeOptions.UsernamePasswordMode, null, null, Password)));

        Assert.Contains("EXCHANGE_USERNAME", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, failure.Message, StringComparison.Ordinal);
        Assert.Null(settings.Stored.Exchange);
        Assert.Empty(secrets.Targets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://mail.contoso.com/EWS/Exchange.asmx")]
    [InlineData("https://mail.example.com/EWS/Exchange.asmx")]
    public async Task Save_WithAnInvalidEndpoint_IsRejectedWithoutSideEffects(string ewsUrl)
    {
        var service = CreateService(out var settings, out var secrets);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new ExchangeSettingsUpdate(ewsUrl, ExchangeOptions.WindowsMode, null, null, null)));

        Assert.Null(settings.Stored.Exchange);
        Assert.Empty(secrets.Targets);
    }

    [Theory]
    [InlineData("NTLM")]
    [InlineData("Basic")]
    public async Task Save_WithAnUnsupportedMode_IsRejectedWithoutSideEffects(string mode)
    {
        var service = CreateService(out var settings, out var secrets);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new ExchangeSettingsUpdate(Url, mode, "mail-bot", null, Password)));

        Assert.Contains("Authentication", failure.Message, StringComparison.Ordinal);
        Assert.Null(settings.Stored.Exchange);
        Assert.Empty(secrets.Targets);
    }

    [Fact]
    public async Task Save_WithABlankMode_FallsBackToTheDocumentedWindowsDefault()
    {
        var service = CreateService(out var settings, out _);

        await service.SaveAsync(new ExchangeSettingsUpdate(Url, "  ", null, null, Password));

        Assert.Equal(ExchangeOptions.WindowsMode, settings.Stored.Exchange?.Authentication);
    }

    [Fact]
    public async Task Save_KeepsTheAiSectionUntouched()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(null, new AiUserSettings("https://provider.test/v1", "gpt-5")));
        var service = CreateService(settings, new InMemorySecretStore());

        await service.SaveAsync(new ExchangeSettingsUpdate(Url, ExchangeOptions.WindowsMode, null, null, null));

        Assert.Equal("https://provider.test/v1", settings.Stored.Ai?.BaseUrl);
        Assert.Equal("gpt-5", settings.Stored.Ai?.Model);
    }

    [Fact]
    public async Task Save_WithoutAnEndpoint_ButWithAnExistingOne_KeepsTheStoredEndpoint()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(UserSettings(), null));
        var service = CreateService(settings, new InMemorySecretStore());

        await service.SaveAsync(new ExchangeSettingsUpdate(null, ExchangeOptions.WindowsMode, null, null, null));

        Assert.Equal(Url, settings.Stored.Exchange?.EwsUrl);
    }

    [Fact]
    public async Task Delete_RemovesTheConfigurationAndThePassword_AndKeepsAi()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            UserSettings(ExchangeOptions.UsernamePasswordMode, "mail-bot", null),
            new AiUserSettings("https://provider.test/v1", null)));
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretTargets.ExchangePassword, Password);
        var service = CreateService(settings, secrets);

        await service.DeleteAsync();

        Assert.Null(settings.Stored.Exchange);
        Assert.Equal("https://provider.test/v1", settings.Stored.Ai?.BaseUrl);
        Assert.Empty(secrets.Targets);
    }

    [Fact]
    public async Task Delete_WithoutAnySavedConfiguration_IsANoOp()
    {
        var service = CreateService(out var settings, out var secrets);

        await service.DeleteAsync();

        Assert.Null(settings.Stored.Exchange);
        Assert.Empty(secrets.Targets);
    }

    // ------------------------------------------------------------------
    // Connection test drafts (never persisted, never a credential mix-up)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveTestOptions_TheDraftOverridesTheEffectiveConfiguration()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(UserSettings(), null));
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretTargets.ExchangePassword, Password);
        var service = CreateServiceWithEffectiveConfiguration(settings, secrets, EnvironmentOptions());

        var draft = await service.ResolveTestOptionsAsync(new ExchangeSettingsUpdate(
            "https://draft.contoso.com/EWS/Exchange.asmx",
            ExchangeOptions.UsernamePasswordMode,
            "draft-bot",
            "DRAFT",
            "draft-pw"));

        Assert.Equal("https://draft.contoso.com/EWS/Exchange.asmx", draft.EwsUrl);
        Assert.Equal(ExchangeOptions.UsernamePasswordMode, draft.Authentication);
        Assert.Equal("draft-bot", draft.Username);
        Assert.Equal("DRAFT", draft.Domain);
        Assert.Equal("draft-pw", draft.Password);
        // Deployment-owned knobs keep their configured values.
        Assert.Equal("mailbox@contoso.com", draft.Mailbox);
        Assert.Equal(42, draft.TimeoutSeconds);
    }

    [Fact]
    public async Task ResolveTestOptions_FallsBackToTheSavedConfigurationAndStoredPassword()
    {
        var settings = new InMemoryUserSettingsStore();
        await settings.SaveAsync(new UserSettings(
            UserSettings(ExchangeOptions.UsernamePasswordMode, "mail-bot", "CONTOSO"),
            null));
        var secrets = new InMemorySecretStore();
        await secrets.WriteAsync(SecretTargets.ExchangePassword, Password);
        var service = CreateServiceWithEffectiveConfiguration(settings, secrets);

        var draft = await service.ResolveTestOptionsAsync(update: null);

        Assert.Equal(Url, draft.EwsUrl);
        Assert.Equal("mail-bot", draft.Username);
        Assert.Equal("CONTOSO", draft.Domain);
        Assert.Equal(Password, draft.Password);
    }

    [Fact]
    public async Task ResolveTestOptions_WindowsMode_NeverCarriesCredentials()
    {
        var service = CreateService(
            out _,
            out _,
            EnvironmentOptions(ExchangeOptions.UsernamePasswordMode, "deploy-bot", "env-pw"));

        var draft = await service.ResolveTestOptionsAsync(new ExchangeSettingsUpdate(
            Url,
            ExchangeOptions.WindowsMode,
            "mail-bot",
            "CONTOSO",
            Password));

        Assert.Equal(ExchangeOptions.WindowsMode, draft.Authentication);
        Assert.Null(draft.Username);
        Assert.Null(draft.Domain);
        Assert.Null(draft.Password);
    }

    [Fact]
    public async Task ResolveTestOptions_UsernamePasswordWithoutAnyPassword_IsRejected()
    {
        var service = CreateService(out _, out _);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveTestOptionsAsync(new ExchangeSettingsUpdate(
                Url,
                ExchangeOptions.UsernamePasswordMode,
                "mail-bot",
                null,
                Password: null)));

        Assert.Contains("EXCHANGE_PASSWORD", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveTestOptions_WithTheSamplePlaceholderEndpoint_IsRejected()
    {
        var service = CreateService(out _, out _);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveTestOptionsAsync(new ExchangeSettingsUpdate(
                "https://mail.example.com/EWS/Exchange.asmx",
                ExchangeOptions.WindowsMode,
                null,
                null,
                null)));
    }
}
