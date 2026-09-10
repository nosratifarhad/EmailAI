namespace EmailAI.Api.Web;

/// <summary>
/// A folder as shown in the UI. <see cref="Key"/> is the REST folder key accepted by
/// GET /api/folders/{folderKey}/messages. Keys intentionally match
/// Infrastructure/Exchange/FolderMapping.cs - the UI must not invent new ones.
/// </summary>
public sealed record FolderInfo(string Key, string Label)
{
    /// <summary>Folders whose rows are about recipients rather than the sender.</summary>
    public bool ShowsRecipients => Key is "sent" or "drafts";
}

/// <summary>
/// The small, stable set of folders the backend exposes. Ordered as they appear in the UI.
/// </summary>
public static class FolderCatalog
{
    public static readonly IReadOnlyList<FolderInfo> All =
    [
        new FolderInfo("inbox", "Inbox"),
        new FolderInfo("sent", "Sent"),
        new FolderInfo("drafts", "Drafts"),
        new FolderInfo("deleted", "Deleted"),
        new FolderInfo("junk", "Junk"),
        new FolderInfo("archive", "Archive"),
    ];

    public static FolderInfo Inbox { get; } = All[0];
}
