using EmailAI.Infrastructure.Exchange;

namespace EmailAI.Tests;

/// <summary>
/// The identity of a custom (user-created) Exchange folder as it travels through the REST surface:
/// <c>folder:</c> + the hex of the Exchange folder id. This is what makes a custom folder
/// addressable and lets two folders with the SAME display name stay different folders. Discovery
/// and message retrieval are covered by <see cref="MailFolderEndpointsTests"/>.
/// </summary>
public sealed class CustomFolderKeyTests
{
    /// <summary>An EWS folder id: base64, so it contains the characters that make a URL key awkward.</summary>
    private const string EwsFolderId = "AQMkADYy/abc+def==XYZ";

    [Fact]
    public void RoundTrip_PreservesTheExchangeFolderIdExactly()
    {
        var key = CustomFolderKey.FromUniqueId(EwsFolderId);

        Assert.True(CustomFolderKey.TryGetUniqueId(key, out var uniqueId));
        Assert.Equal(EwsFolderId, uniqueId);
    }

    [Theory]
    [InlineData("AAA=")]
    [InlineData("a/b+c==")]
    [InlineData("space and tab\there")]
    [InlineData("-=?&%#")]
    public void RoundTrip_PreservesAnyFolderId(string uniqueId)
    {
        Assert.True(CustomFolderKey.TryGetUniqueId(CustomFolderKey.FromUniqueId(uniqueId), out var decoded));
        Assert.Equal(uniqueId, decoded);
    }

    [Fact]
    public void Key_IsUrlSafe_AndNeverCarriesTheFolderName()
    {
        var key = CustomFolderKey.FromUniqueId(EwsFolderId);

        Assert.StartsWith(CustomFolderKey.Prefix, key, StringComparison.Ordinal);
        Assert.True(FolderMapping.IsSupported(key));

        // Nothing that needs percent-encoding in a URL path segment, and no '/' - the character a
        // route value keeps literal.
        Assert.Matches("^folder:[0-9A-F]+$", key);
        Assert.DoesNotContain("/", key, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFoldersWithTheSameName_HaveDifferentKeys()
    {
        // Identity is the folder id, so a rename (or two folders called "Archive") can never select
        // the wrong folder.
        var first = CustomFolderKey.FromUniqueId(EwsFolderId);
        var second = CustomFolderKey.FromUniqueId("AQMkADYy/abc+def==XYz"); // differs in one character's case

        Assert.NotEqual(first, second);
        Assert.NotEqual(first.ToUpperInvariant(), second.ToUpperInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("inbox")]
    [InlineData("sent")]
    [InlineData("folder:")]
    [InlineData("folder:0")]
    [InlineData("folder:zz")]
    [InlineData("folder:not-hex!")]
    [InlineData("folder:AQMkADYy/abc")]
    public void MalformedKeys_AreRejected(string? folderKey)
    {
        Assert.False(CustomFolderKey.TryGetUniqueId(folderKey, out var uniqueId));
        Assert.Equal(string.Empty, uniqueId);
    }

    [Fact]
    public void CustomKeysAndWellKnownKeys_NeverOverlap_ButBothAreSupported()
    {
        var customKey = CustomFolderKey.FromUniqueId(EwsFolderId);

        Assert.False(FolderMapping.TryResolve(customKey, out _));
        Assert.False(CustomFolderKey.TryGetUniqueId("inbox", out _));

        Assert.True(FolderMapping.IsSupported("inbox"));
        Assert.True(FolderMapping.IsSupported("INBOX"));
        Assert.True(FolderMapping.IsSupported(customKey));

        Assert.False(FolderMapping.IsSupported("nonsense"));
        Assert.False(FolderMapping.IsSupported("folder:not-hex"));
    }
}
