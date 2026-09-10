using Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// The REST folder key of a user-created (custom) Exchange folder:
/// <c>folder:</c> followed by the hex encoding of the UTF-8 bytes of the Exchange folder id.
///
/// Why an encoded key instead of the id itself:
/// <list type="bullet">
/// <item>An EWS folder id is base64 and contains '/', '+' and '=', which are awkward or special in
/// URL path segments and route values.</item>
/// <item>Hex is <b>case-insensitive-safe</b>: the app maps a few folder-keyed caches
/// (<c>MailNotificationService</c>, the desktop-notification feed) with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>, and an all-uppercase alphabet can never make two
/// different folder ids compare equal.</item>
/// <item>The key stays opaque: it carries no name, so renaming a folder in Outlook never changes
/// the identity the UI selected.</item>
/// </list>
/// The key is an Exchange folder identifier, not a secret: it is used only to address that folder
/// through the same authenticated Exchange connection the mailbox already uses.
/// </summary>
internal static class CustomFolderKey
{
    /// <summary>Marks a REST folder key as a custom (user-created) file folder.</summary>
    public const string Prefix = "folder:";

    /// <summary>Builds the REST folder key for an Exchange folder id.</summary>
    public static string FromUniqueId(string uniqueId)
        => Prefix + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(uniqueId));

    /// <summary>
    /// Decodes a custom-folder key back to the Exchange folder id. False for anything that is not a
    /// well-formed custom key (a well-known key, a blank/truncated key, a non-hex payload), so a
    /// malformed key is rejected as a bad request instead of reaching Exchange.
    /// </summary>
    public static bool TryGetUniqueId(string? folderKey, out string uniqueId)
    {
        uniqueId = string.Empty;
        if (folderKey is null)
        {
            return false;
        }

        var key = folderKey.Trim();
        if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var encoded = key[Prefix.Length..];
        if (encoded.Length == 0 || encoded.Length % 2 != 0 || !IsHex(encoded))
        {
            return false;
        }

        uniqueId = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(encoded));
        return uniqueId.Length > 0;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
