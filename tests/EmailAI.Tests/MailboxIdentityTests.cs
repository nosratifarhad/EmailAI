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
            "  Farhad Nosrati  ",
            " farhad@contoso.com ",
            "   ",
            MailboxIdentity.ExchangeDirectorySource);

        Assert.Equal("Farhad Nosrati", identity.DisplayName);
        Assert.Equal("farhad@contoso.com", identity.SmtpAddress);
        Assert.Null(identity.AccountName);
        Assert.Equal(MailboxIdentity.ExchangeDirectorySource, identity.Source);
        Assert.True(identity.IsKnown);
    }

    [Fact]
    public void Create_UnwrapsADisplayNameThatArrivesAsNamePlusAddress()
    {
        var identity = MailboxIdentity.Create(
            "Farhad Nosrati <farhad@contoso.com>",
            null,
            null,
            MailboxIdentity.WindowsIdentitySource);

        Assert.Equal("Farhad Nosrati", identity.DisplayName);
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
            "Farhad Nosrati",
            "farhad@contoso.com",
            "CONTOSO\\farhad",
            MailboxIdentity.ExchangeDirectorySource);

        var aliases = identity.Aliases;

        Assert.Equal("Farhad Nosrati", aliases[0]);
        Assert.Contains("farhad@contoso.com", aliases);
        Assert.Contains("Farhad", aliases);
        Assert.Contains("Nosrati", aliases);
        Assert.Contains("CONTOSO\\farhad", aliases);
        // Deduplication is case-insensitive, so the account's user part ("farhad") is already
        // covered by the display-name part "Farhad" instead of being repeated.
        Assert.Single(aliases, alias => string.Equals(alias, "farhad", StringComparison.OrdinalIgnoreCase));
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
            "Farhad Nosrati",
            "farhad@contoso.com",
            "farhad",
            MailboxIdentity.ExchangeDirectorySource);

        var description = identity.Describe();

        Assert.Contains("Farhad Nosrati", description, StringComparison.Ordinal);
        Assert.Contains("farhad@contoso.com", description, StringComparison.Ordinal);
        Assert.Contains(MailboxIdentity.ExchangeDirectorySource, description, StringComparison.Ordinal);
    }
}
