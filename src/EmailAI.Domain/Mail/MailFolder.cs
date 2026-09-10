namespace EmailAI.Domain.Mail;

/// <summary>
/// One mailbox folder as the UI sees it: a well-known system folder (Inbox/Sent/...) or a
/// user-created custom folder discovered in Exchange.
///
/// <see cref="Id"/> is the REST folder key (<c>GET /api/folders/{folderKey}/...</c>). For a
/// well-known folder it is the well-known key ("inbox", "sent", ...); for a user-created folder it
/// is an opaque key derived from the Exchange folder id. Identity is <b>never</b> the display
/// name: Exchange folder names are neither unique nor stable.
/// </summary>
public sealed class MailFolder
{
    /// <summary>The REST folder key (a well-known key, or a custom-folder key).</summary>
    public required string Id { get; init; }

    /// <summary>The folder name shown in the sidebar. Never blank.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The <see cref="Id"/> of the folder this one is nested under (null at top level).</summary>
    public string? ParentId { get; init; }

    /// <summary>
    /// The folder's well-known type (see <see cref="MailFolderTypes"/>) - a well-known folder key
    /// for a system folder, <see cref="MailFolderTypes.Custom"/> for a user-created one.
    /// </summary>
    public string WellKnownType { get; init; } = MailFolderTypes.Custom;

    /// <summary>True when Exchange reports at least one child folder below this one.</summary>
    public bool HasChildren { get; init; }
}

/// <summary>
/// The folder types <see cref="MailFolder.WellKnownType"/> can carry. The well-known names are the
/// same strings as the well-known REST folder keys, so a folder of this type is addressable by it.
/// </summary>
public static class MailFolderTypes
{
    /// <summary>A user-created folder (what custom-folder discovery returns).</summary>
    public const string Custom = "custom";

    public const string Inbox = "inbox";
    public const string Sent = "sent";
    public const string Drafts = "drafts";
    public const string Deleted = "deleted";
    public const string Junk = "junk";
    public const string Archive = "archive";

    /// <summary>
    /// The well-known type for a well-known REST folder key, or <see cref="Custom"/> when the key
    /// is not one of them.
    /// </summary>
    public static string ForFolderKey(string folderKey) => folderKey?.Trim().ToLowerInvariant() switch
    {
        Inbox => Inbox,
        Sent => Sent,
        Drafts => Drafts,
        Deleted => Deleted,
        Junk => Junk,
        Archive => Archive,
        _ => Custom,
    };
}
