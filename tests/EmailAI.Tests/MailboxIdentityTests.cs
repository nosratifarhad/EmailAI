using EmailAI.Domain.Exchange;

namespace EmailAI.Tests;

/// <summary>
/// The AUTHORITATIVE current-user identity: normalisation, alias derivation and the
/// "unknown" fallback. The identity comes from the Exchange account EmailAI authenticated
/// with - never from an email - and never carries a secret.
/// </summary>
public class MailboxIdentityTests
{
    [Fact]
    public void Create_TrimsValues_AndTurnsBlankIntoNull()
    {
        var identity = MailboxIdentity.Create(
            "  Alex Doe  ",
            " alex@contoso.com ",
            "   ",
            MailboxIdentity.ExchangeDirectorySource);

        Assert.Equal("Alex Doe", identity.DisplayName);
        Assert.Equal("alex@contoso.com", identity.SmtpAddress);
        Assert.Null(identity.AccountName);
        Assert.Equal(MailboxIdentity.ExchangeDirectorySource, identity.Source);
        Assert.True(identity.IsKnown);
    }

    [Fact]
    public void Create_UnwrapsADisplayNameThatArrivesAsNamePlusAddress()
    {
        var identity = MailboxIdentity.Create(
            "Alex Doe <alex@contoso.com>",
            null,
            null,
            MailboxIdentity.WindowsIdentitySource);

        Assert.Equal("Alex Doe", identity.DisplayName);
    }

    [Fact]
    public void Create_WithoutAUsableSource_FallsBackToUnknownSource()
    {
        var identity = MailboxIdentity.Create("Name", null, null, "   ");

        Assert.Equal(MailboxIdentity.UnknownSource, identity.Source);
    }

    [Fact]
    public void None_IsNotKnown_AndHasNoAliases()
    {
        Assert.False(MailboxIdentity.None.IsKnown);
        Assert.Empty(MailboxIdentity.None.Aliases);
        Assert.Contains("unknown", MailboxIdentity.None.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Aliases_StartWithTheDisplayName_AndCoverNameAccountAndSmtpLocalPart()
    {
        var identity = MailboxIdentity.Create(
            "Alex Doe",
            "alex@contoso.com",
            "CONTOSO\\alex",
            MailboxIdentity.ExchangeDirectorySource);

        var aliases = identity.Aliases;

        Assert.Equal("Alex Doe", aliases[0]);
        Assert.Contains("alex@contoso.com", aliases);
        Assert.Contains("Alex", aliases);
        Assert.Contains("Doe", aliases);
        Assert.Contains("CONTOSO\\alex", aliases);
        // Deduplication is case-insensitive, so the account's user part ("alex") is already
        // covered by the display-name part "Alex" instead of being repeated.
        Assert.Single(aliases, alias => string.Equals(alias, "alex", StringComparison.OrdinalIgnoreCase));
        Assert.True(aliases.Count <= MailboxAliases.MaxAliases);
    }

    [Fact]
    public void Aliases_AreCappedAndDeduplicated()
    {
        var identity = MailboxIdentity.Create(
            "aaa bbb ccc ddd eee fff ggg hhh iii jjj kkk",
            "aaa.bbb@contoso.com",
            "aaa",
            MailboxIdentity.ConfiguredAccountSource);

        var aliases = identity.Aliases;

        Assert.True(aliases.Count <= MailboxAliases.MaxAliases);
        Assert.Equal(aliases.Count, aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Aliases_DropSingleCharacterNoise()
    {
        var identity = MailboxIdentity.Create("a b c", "ab@contoso.com", null, MailboxIdentity.WindowsIdentitySource);

        Assert.All(identity.Aliases, alias => Assert.True(alias.Length >= 2, $"'{alias}' is too short"));
        Assert.DoesNotContain("a", identity.Aliases);
        Assert.Contains("ab", identity.Aliases);
    }

    [Fact]
    public void Describe_NamesTheIdentityAndItsSource_WithoutAnySecret()
    {
        var identity = MailboxIdentity.Create(
            "Alex Doe",
            "alex@contoso.com",
            "alex",
            MailboxIdentity.ExchangeDirectorySource);

        var description = identity.Describe();

        Assert.Contains("Alex Doe", description, StringComparison.Ordinal);
        Assert.Contains("alex@contoso.com", description, StringComparison.Ordinal);
        Assert.Contains(MailboxIdentity.ExchangeDirectorySource, description, StringComparison.Ordinal);
    }
}
