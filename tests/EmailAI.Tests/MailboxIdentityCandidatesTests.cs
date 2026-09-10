using EmailAI.Domain.Exchange;
using EmailAI.Infrastructure.Exchange;

namespace EmailAI.Tests;

/// <summary>
/// The values EWS <c>ResolveNames</c> is asked for, when the current-user identity is resolved
/// through the Exchange directory.
///
/// This matters because the form of the name decides whether the directory answers at all: a
/// live Exchange directory returns NO RESULTS for <c>DOMAIN\user</c> but resolves the bare
/// account name to the authoritative display name and SMTP address. Sending only the qualified
/// form silently degrades the identity the AI is told about, so both forms are tried - and the
/// most specific source (an impersonated mailbox or a configured account) always comes first.
/// </summary>
public class MailboxIdentityCandidatesTests
{
    private static ExchangeOptions WindowsOptions(string? mailbox = null) => new()
    {
        EwsUrl = "https://ews.example.org/EWS/Exchange.asmx",
        Authentication = ExchangeOptions.WindowsMode,
        Mailbox = mailbox,
        ExchangeVersion = "Exchange2013_SP1",
        TimeoutSeconds = 5,
    };

    [Fact]
    public void WindowsAccount_IsTriedQualifiedAndBare_MostSpecificFirst()
    {
        var local = MailboxIdentity.Create(null, null, "CONTOSO\\alex.doe", MailboxIdentity.WindowsIdentitySource);

        var candidates = EwsMailboxIdentityProvider.ResolveCandidates(WindowsOptions(), local);

        Assert.Equal(["CONTOSO\\alex.doe", "alex.doe"], candidates);
    }

    [Fact]
    public void ConfiguredMailbox_IsTriedBeforeTheWindowsAccount()
    {
        var local = MailboxIdentity.Create(null, null, "CONTOSO\\svc-mail", MailboxIdentity.WindowsIdentitySource);

        var candidates = EwsMailboxIdentityProvider.ResolveCandidates(
            WindowsOptions("alex@contoso.com"),
            local);

        Assert.Equal(["alex@contoso.com", "CONTOSO\\svc-mail", "svc-mail"], candidates);
    }

    [Fact]
    public void UsernamePasswordAccount_IsTriedAsConfiguredAndBare()
    {
        var options = new ExchangeOptions
        {
            EwsUrl = "https://ews.example.org/EWS/Exchange.asmx",
            Authentication = ExchangeOptions.UsernamePasswordMode,
            Domain = "CONTOSO",
            Username = "alex",
            Password = "not-used-here",
            ExchangeVersion = "Exchange2013_SP1",
            TimeoutSeconds = 5,
        };
        var local = MailboxIdentity.Create(
            null,
            null,
            "CONTOSO\\alex",
            MailboxIdentity.ConfiguredAccountSource);

        var candidates = EwsMailboxIdentityProvider.ResolveCandidates(options, local);

        Assert.Equal(["alex", "CONTOSO\\alex"], candidates);
    }

    [Fact]
    public void UnknownIdentity_ProducesNoCandidates()
    {
        Assert.Empty(EwsMailboxIdentityProvider.ResolveCandidates(WindowsOptions(), MailboxIdentity.None));
        Assert.Empty(EwsMailboxIdentityProvider.ResolveCandidates(null, MailboxIdentity.None));
    }

    [Fact]
    public void DuplicateForms_AreNotSentTwice()
    {
        var local = MailboxIdentity.Create(null, null, "alex@contoso.com", MailboxIdentity.WindowsIdentitySource);

        var candidates = EwsMailboxIdentityProvider.ResolveCandidates(WindowsOptions(), local);

        Assert.Equal(["alex@contoso.com"], candidates);
    }

    [Fact]
    public void WindowsAccountWithoutADomain_IsUsedAsItIs()
    {
        var local = MailboxIdentity.Create(null, null, "alex", MailboxIdentity.WindowsIdentitySource);

        var candidates = EwsMailboxIdentityProvider.ResolveCandidates(WindowsOptions(), local);

        Assert.Equal(["alex"], candidates);
    }
}
