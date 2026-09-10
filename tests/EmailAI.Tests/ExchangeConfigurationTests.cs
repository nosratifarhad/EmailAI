using EmailAI.Application.Exceptions;
using EmailAI.Domain.Exchange;
using EmailAI.Infrastructure.Exchange;
using Ews = Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Tests;

/// <summary>
/// Exchange configuration semantics: example-host placeholders are never treated as
/// real endpoints, and the explicit authentication mode is validated up front
/// (Windows needs no credentials; UsernamePassword requires username AND password).
/// </summary>
public sealed class ExchangeConfigurationTests
{
    [Theory]
    [InlineData("https://mail.example.com/EWS/Exchange.asmx", true)]
    [InlineData("https://example.com/EWS/Exchange.asmx", true)]
    [InlineData("https://autodiscover.example.com/EWS/Exchange.asmx", true)]
    [InlineData("http://example.org/EWS/Exchange.asmx", true)]
    [InlineData("https://example.net/EWS/Exchange.asmx", true)]
    [InlineData("https://ews.example.org:8443/EWS/Exchange.asmx", true)]
    [InlineData("https://mail.contoso.com/EWS/Exchange.asmx", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    public void IsSamplePlaceholder_DetectsReservedDocumentationHosts(string? url, bool expected)
        => Assert.Equal(expected, ExchangeOptions.IsSamplePlaceholder(url));

    [Fact]
    public void Authentication_DefaultsToWindowsMode()
    {
        var options = new ExchangeOptions();
        Assert.Equal(ExchangeOptions.WindowsMode, options.Authentication);
        Assert.True(options.UsesWindowsAuthentication);
        Assert.False(options.UsesUsernamePasswordAuthentication);
    }

    [Theory]
    [InlineData("Windows", true)]
    [InlineData("WINDOWS", true)]
    [InlineData("windows", true)]
    [InlineData("UsernamePassword", false)]
    [InlineData("USERNAMEPASSWORD", false)]
    [InlineData("usernamepassword", false)]
    public void Authentication_ModeHelpers_AreCaseInsensitive(string mode, bool expectsWindows)
    {
        var options = new ExchangeOptions { Authentication = mode };
        Assert.Equal(expectsWindows, options.UsesWindowsAuthentication);
        Assert.Equal(!expectsWindows, options.UsesUsernamePasswordAuthentication);
    }

    [Fact]
    public void Factory_RejectsSamplePlaceholderAsConfigurationError()
    {
        var options = new ExchangeOptions
        {
            EwsUrl = "https://mail.example.com/EWS/Exchange.asmx",
            Authentication = ExchangeOptions.WindowsMode,
            ExchangeVersion = "Exchange2013_SP1",
            TimeoutSeconds = 10,
        };

        var failure = Assert.Throws<ExchangeMailException>(() =>
            ExchangeServiceFactory.Create(options, ExchangeAuthMode.Windows));

        Assert.Equal(ExchangeMailErrorKind.Configuration, failure.Kind);
        Assert.Contains("EXCHANGE_EWS_URL", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NTLM")]
    [InlineData("BASIC")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Hybrid")]
    [InlineData(null)]
    public void GetConfigurationError_RejectsUnsupportedAuthenticationModes(string? mode)
    {
        var options = new ExchangeOptions { Authentication = mode ?? string.Empty };
        var error = ExchangeOptions.GetConfigurationError(options);

        Assert.NotNull(error);
        Assert.Contains("EXCHANGE_AUTHENTICATION", error, StringComparison.Ordinal);
        Assert.Contains("Windows", error, StringComparison.Ordinal);
        Assert.Contains("UsernamePassword", error, StringComparison.Ordinal);
    }

    [Fact]
    public void GetConfigurationError_WindowsMode_DoesNotRequireUsernameOrPassword()
    {
        // Windows mode must work with no credentials at all...
        var bare = new ExchangeOptions { Authentication = ExchangeOptions.WindowsMode };
        Assert.Null(ExchangeOptions.GetConfigurationError(bare));

        // ...and configured credentials stay optional (they are ignored, never required).
        var withCredentials = new ExchangeOptions
        {
            Authentication = ExchangeOptions.WindowsMode,
            Username = "mail-bot",
            Password = "secret",
        };
        Assert.Null(ExchangeOptions.GetConfigurationError(withCredentials));
    }

    [Fact]
    public void GetConfigurationError_UsernamePasswordMode_RequiresUsername()
    {
        var options = new ExchangeOptions
        {
            Authentication = ExchangeOptions.UsernamePasswordMode,
            Password = "secret",
        };

        var error = ExchangeOptions.GetConfigurationError(options);

        Assert.NotNull(error);
        Assert.Contains("EXCHANGE_USERNAME", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error, StringComparison.Ordinal);
    }

    [Fact]
    public void GetConfigurationError_UsernamePasswordMode_RequiresPassword()
    {
        var options = new ExchangeOptions
        {
            Authentication = ExchangeOptions.UsernamePasswordMode,
            Username = "mail-bot",
        };

        var error = ExchangeOptions.GetConfigurationError(options);

        Assert.NotNull(error);
        Assert.Contains("EXCHANGE_PASSWORD", error, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-bot", error, StringComparison.Ordinal);
    }

    [Fact]
    public void GetConfigurationError_UsernamePasswordMode_ValidWhenUsernameAndPasswordPresent()
    {
        var options = new ExchangeOptions
        {
            Authentication = ExchangeOptions.UsernamePasswordMode,
            Domain = "EXAMPLE",
            Username = "mail-bot",
            Password = "secret",
        };

        Assert.Null(ExchangeOptions.GetConfigurationError(options));
    }

    [Fact]
    public void BuildAttempts_InvalidMode_ThrowsConfigurationError_BeforeAnyExchangeAttempt()
    {
        var options = new ExchangeOptions
        {
            EwsUrl = "https://exch.example.test/EWS/Exchange.asmx",
            Authentication = "NTLM",
        };

        var failure = Assert.Throws<ExchangeMailException>(() => ExchangeAuthRunner.BuildAttempts(options));

        Assert.Equal(ExchangeMailErrorKind.Configuration, failure.Kind);
        Assert.Contains(ExchangeOptions.WindowsMode, failure.Message, StringComparison.Ordinal);
        Assert.Contains(ExchangeOptions.UsernamePasswordMode, failure.Message, StringComparison.Ordinal);
    }


    // ------------------------------------------------------------------
    // Factory wiring for the two explicit modes
    // ------------------------------------------------------------------

    private static ExchangeOptions ValidOptions(string mode, string? username = null, string? password = null)
        => new()
        {
            EwsUrl = "https://exch.example.test/EWS/Exchange.asmx",
            Authentication = mode,
            Domain = "EXAMPLE",
            Username = username,
            Password = password,
            ExchangeVersion = "Exchange2013_SP1",
            TimeoutSeconds = 5,
        };

    [Fact]
    public void Factory_WindowsMode_UsesDefaultCredentials_AndNeverUsesExplicitCredentials()
    {
        // Explicit credentials are present in the options but must never be applied:
        // Windows mode authenticates with the current Windows identity only.
        var service = ExchangeServiceFactory.Create(
            ValidOptions(ExchangeOptions.WindowsMode, username: "mail-bot", password: "secret"),
            ExchangeAuthMode.Windows);

        Assert.True(service.UseDefaultCredentials);
        Assert.Null(service.Credentials);
    }

    [Fact]
    public void Factory_UsernamePasswordMode_RejectsMissingUsername_AsConfigurationError()
    {
        var failure = Assert.Throws<ExchangeMailException>(() =>
            ExchangeServiceFactory.Create(
                ValidOptions(ExchangeOptions.UsernamePasswordMode, username: null, password: "secret"),
                ExchangeAuthMode.UsernamePassword));

        Assert.Equal(ExchangeMailErrorKind.Configuration, failure.Kind);
        Assert.Contains("EXCHANGE_USERNAME", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_UsernamePasswordMode_RejectsMissingPassword_AsConfigurationError()
    {
        var failure = Assert.Throws<ExchangeMailException>(() =>
            ExchangeServiceFactory.Create(
                ValidOptions(ExchangeOptions.UsernamePasswordMode, username: "mail-bot", password: null),
                ExchangeAuthMode.UsernamePassword));

        Assert.Equal(ExchangeMailErrorKind.Configuration, failure.Kind);
        Assert.Contains("EXCHANGE_PASSWORD", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mail-bot", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_UsernamePasswordMode_CarriesExplicitCredentials_AndNoWindowsIdentity()
    {
        var service = ExchangeServiceFactory.Create(
            ValidOptions(ExchangeOptions.UsernamePasswordMode, username: "mail-bot", password: "secret"),
            ExchangeAuthMode.UsernamePassword);

        Assert.False(service.UseDefaultCredentials);
        var credentials = Assert.IsType<Ews.WebCredentials>(service.Credentials);
        var account = Assert.IsAssignableFrom<System.Net.NetworkCredential>(credentials.Credentials);
        Assert.Equal("mail-bot", account.UserName);
        Assert.Equal("secret", account.Password);
        Assert.Equal("EXAMPLE", account.Domain);
    }
}

