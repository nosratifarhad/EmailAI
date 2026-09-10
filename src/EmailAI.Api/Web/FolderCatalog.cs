using EmailAI.Domain.Mail;

namespace EmailAI.Api.Web;

/// <summary>
/// A folder as shown in the UI. <see cref="Key"/> is the REST folder key accepted by
/// GET /api/folders/{folderKey}/messages (a well-known key from <see cref="FolderCatalog"/> or a
/// custom-folder key discovered in Exchange). Keys match
/// Infrastructure/Exchange/FolderMapping.cs and CustomFolderKey.cs - the UI must not invent new ones.
/// </summary>
public sealed record FolderInfo(string Key, string Label)
{
    /// <summary>
    /// The folder's well-known type (<see cref="MailFolderTypes"/>). It is what decides how a row is
    /// read - a system folder knows it is Sent/Drafts, a custom folder does not.
    /// </summary>
    public string WellKnownType { get; init; } = MailFolderTypes.Custom;

    /// <summary>Folders whose rows are about recipients rather than the sender.</summary>
    public bool ShowsRecipients =>
        WellKnownType is MailFolderTypes.Sent or MailFolderTypes.Drafts;

    /// <summary>
    /// The sidebar row for a folder discovered in Exchange. The display name is used for display
    /// only and falls back to a placeholder, so a nameless folder can never render a blank row.
    /// </summary>
    public static FolderInfo FromMailFolder(MailFolder folder) => new(
        folder.Id,
        string.IsNullOrWhiteSpace(folder.DisplayName) ? "(unnamed folder)" : folder.DisplayName.Trim())
    {
        WellKnownType = string.IsNullOrWhiteSpace(folder.WellKnownType)
            ? MailFolderTypes.Custom
            : folder.WellKnownType,
    };
}

/// <summary>
/// The small, stable set of folders the backend always exposes, ordered as they appear in the UI.
/// Custom (user-created) folders are NOT listed here: they are discovered from the user's mailbox.
/// </summary>
public static class FolderCatalog
{
    public static readonly IReadOnlyList<FolderInfo> All =
    [
        WellKnown("inbox", "Inbox"),
        WellKnown("sent", "Sent"),
        WellKnown("drafts", "Drafts"),
        WellKnown("deleted", "Deleted"),
        WellKnown("junk", "Junk"),
        WellKnown("archive", "Archive"),
    ];

    public static FolderInfo Inbox { get; } = All[0];

    private static FolderInfo WellKnown(string key, string label) =>
        new(key, label) { WellKnownType = MailFolderTypes.ForFolderKey(key) };
}

