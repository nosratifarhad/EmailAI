using Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// Maps the well-known folder keys used by the REST surface to EWS well-known folders. A key that
/// is not in this set is either a custom (user-created) folder - addressed through
/// <see cref="CustomFolderKey"/> - or not a folder key at all.
/// </summary>
internal static class FolderMapping
{
    private static readonly IReadOnlyDictionary<string, WellKnownFolderName> Folders =
        new Dictionary<string, WellKnownFolderName>(StringComparer.OrdinalIgnoreCase)
        {
            ["inbox"] = WellKnownFolderName.Inbox,
            ["sent"] = WellKnownFolderName.SentItems,
            ["drafts"] = WellKnownFolderName.Drafts,
            ["deleted"] = WellKnownFolderName.DeletedItems,
            ["junk"] = WellKnownFolderName.JunkEmail,
            ["archive"] = WellKnownFolderName.ArchiveInbox,
        };

    public static bool TryResolve(string folderKey, out WellKnownFolderName folder)
    {
        if (!string.IsNullOrWhiteSpace(folderKey) && Folders.TryGetValue(folderKey.Trim(), out folder))
        {
            return true;
        }

        folder = default;
        return false;
    }

    /// <summary>
    /// True when the key addresses a folder this service can open: a well-known folder, or a
    /// well-formed custom-folder key (see <see cref="CustomFolderKey"/>). A malformed key is rejected
    /// here, before any Exchange work.
    /// </summary>
    public static bool IsSupported(string folderKey)
        => TryResolve(folderKey, out _) || CustomFolderKey.TryGetUniqueId(folderKey, out _);

    /// <summary>All accepted well-known folder keys (stable, ordered, documented).</summary>
    public static IReadOnlyList<string> SupportedKeys { get; } =
        ["inbox", "sent", "drafts", "deleted", "junk", "archive"];
}
