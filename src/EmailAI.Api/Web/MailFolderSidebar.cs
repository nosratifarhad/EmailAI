namespace EmailAI.Api.Web;

/// <summary>
/// The state of the folder sidebar: the fixed top-level folders plus the custom (user-created) child
/// folders discovered lazily under Inbox.
///
/// It deliberately owns everything the sidebar renders - which rows are visible, in which order, at
/// which indent, which one offers an expand/collapse arrow and which one is selected - so that
/// "expanding must not change the selection", "a selected child keeps Inbox open" and
/// "collapsing hides the children again" are verifiable without a browser. The component owns only
/// the asynchronous part (when to load the children).
///
/// The hierarchy comes from Exchange through <see cref="FolderInfo.FromMailFolder"/>; nothing here
/// invents a folder, and rows are addressed by <see cref="FolderInfo.Key"/> (never by name).
/// </summary>
public sealed class MailFolderSidebar(IReadOnlyList<FolderInfo> roots)
{
    /// <summary>The one top-level folder that can have custom children (Inbox).</summary>
    public static readonly FolderInfo ExpandableRoot = FolderCatalog.Inbox;

    /// <summary>The fixed top-level folders, in display order.</summary>
    public IReadOnlyList<FolderInfo> Roots { get; } = roots;

    /// <summary>The discovered child folders of <see cref="ExpandableRoot"/>, in display order.</summary>
    public IReadOnlyList<FolderInfo> Children { get; private set; } = [];

    /// <summary>True while the children of the expandable folder are shown.</summary>
    public bool Expanded { get; private set; }

    /// <summary>True while a child-folder request is in flight.</summary>
    public bool ChildrenLoading { get; private set; }

    /// <summary>True once Exchange answered the discovery request (successfully).</summary>
    public bool ChildrenLoaded { get; private set; }

    /// <summary>Why discovery failed, phrased for the user (null while it succeeded or was never tried).</summary>
    public string? ChildrenError { get; private set; }

    /// <summary>
    /// True while the expand/collapse arrow is offered. Discovery is lazy - the first paint makes no
    /// Exchange call - so the arrow is offered until a successful discovery proves there is nothing
    /// to show beneath it.
    /// </summary>
    public bool CanExpand => !ChildrenLoaded || Children.Count > 0;

    /// <summary>Opens the child list (the load itself is the caller's job).</summary>
    public void Expand() => Expanded = true;

    /// <summary>Hides the child list. The selection is untouched.</summary>
    public void Collapse() => Expanded = false;

    /// <summary>Marks a child-folder request as started; any previous error is cleared.</summary>
    public void BeginChildrenLoad()
    {
        ChildrenLoading = true;
        ChildrenError = null;
    }

    /// <summary>
    /// Applies the folders Exchange returned. An empty answer collapses the (now pointless) child
    /// list and hides the arrow; an empty folder is never an error state.
    /// </summary>
    public void ApplyChildren(IReadOnlyList<FolderInfo> children)
    {
        Children = children;
        ChildrenLoaded = true;
        ChildrenLoading = false;
        ChildrenError = null;

        if (children.Count == 0)
        {
            Expanded = false;
        }
    }

    /// <summary>
    /// Records a failed discovery. The child list stays open so the user can read the message and
    /// retry, and the top-level folders keep working: a folder list that cannot be read never blocks
    /// the mail client.
    /// </summary>
    public void FailChildrenLoad(string message)
    {
        ChildrenLoading = false;
        ChildrenError = message;
    }

    /// <summary>
    /// Forgets the discovered hierarchy (used when the Exchange configuration changed, so the next
    /// expand reads the new mailbox instead of showing the previous one's folders).
    /// </summary>
    public void Reset()
    {
        Children = [];
        Expanded = false;
        ChildrenLoading = false;
        ChildrenLoaded = false;
        ChildrenError = null;
    }

    /// <summary>True when the key addresses one of the discovered child folders.</summary>
    public bool IsChild(string folderKey) => Children.Any(child => child.Key == folderKey);

    /// <summary>
    /// Keeps the structure consistent with a selection: selecting a child folder keeps its parent
    /// open, so the selected row stays visible.
    /// </summary>
    public void Select(string folderKey)
    {
        if (IsChild(folderKey))
        {
            Expanded = true;
        }
    }

    /// <summary>
    /// The rows to render, top to bottom: every top-level folder, each followed by its children while
    /// it is expanded. <paramref name="selectedKey"/> is compared against the folder key, so two
    /// folders that share a display name remain distinguishable.
    /// </summary>
    public IReadOnlyList<FolderRow> Rows(string selectedKey)
    {
        var rows = new List<FolderRow>(Roots.Count + Children.Count);

        foreach (var root in Roots)
        {
            var expandable = root.Key == ExpandableRoot.Key && CanExpand;
            rows.Add(new FolderRow(
                root,
                Depth: 0,
                IsSelected: root.Key == selectedKey,
                IsExpandable: expandable,
                IsExpanded: expandable && Expanded,
                IsChild: false));

            if (!expandable || !Expanded)
            {
                continue;
            }

            foreach (var child in Children)
            {
                rows.Add(new FolderRow(
                    child,
                    Depth: 1,
                    IsSelected: child.Key == selectedKey,
                    IsExpandable: false,
                    IsExpanded: false,
                    IsChild: true));
            }
        }

        return rows;
    }
}

/// <summary>
/// One sidebar row: the folder, its indent depth (custom children sit one level below Inbox) and the
/// visual state the markup needs.
/// </summary>
public sealed record FolderRow(
    FolderInfo Folder,
    int Depth,
    bool IsSelected,
    bool IsExpandable,
    bool IsExpanded,
    bool IsChild);
