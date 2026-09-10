using Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// Maps the folder keys used by the REST surface to EWS well-known folders.
/// The UI is deliberately restricted to this small, stable set; Exchange
/// folder discovery comes in a later phase.
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

    /// <summary>All accepted folder keys (stable, ordered, documented).</summary>
    public static IReadOnlyList<string> SupportedKeys { get; } =
        ["inbox", "sent", "drafts", "deleted", "junk", "archive"];
}
