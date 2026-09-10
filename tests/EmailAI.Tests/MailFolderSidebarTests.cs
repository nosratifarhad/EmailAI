using EmailAI.Api.Web;
using EmailAI.Domain.Mail;

namespace EmailAI.Tests;

/// <summary>
/// The folder sidebar's state: what the user sees when Inbox is expanded or collapsed and when a
/// custom folder is selected. The component itself only supplies the asynchronous load, so the
/// Outlook-like behaviour (an arrow that never selects, a selection that survives an expand, an
/// indented hierarchy, a failed discovery that leaves every top-level folder usable) is pinned here
/// without a browser.
/// </summary>
public sealed class MailFolderSidebarTests
{
    private const string CustomersKey = "folder:4142";
    private const string ProjectsKey = "folder:4344";

    private static FolderInfo Customers { get; } = Child(CustomersKey, "Customers");

    private static FolderInfo Projects { get; } = Child(ProjectsKey, "Projects");

    private static FolderInfo Child(string id, string displayName)
        => FolderInfo.FromMailFolder(new MailFolder
        {
            Id = id,
            DisplayName = displayName,
            ParentId = FolderCatalog.Inbox.Key,
        });

    private static MailFolderSidebar Sidebar() => new(FolderCatalog.All);

    /// <summary>A sidebar with Inbox expanded onto the given custom folders.</summary>
    private static MailFolderSidebar Expanded(params FolderInfo[] children)
    {
        var sidebar = Sidebar();
        sidebar.Expand();
        sidebar.BeginChildrenLoad();
        sidebar.ApplyChildren(children);
        return sidebar;
    }

    private static FolderRow Selected(IReadOnlyList<FolderRow> rows)
    {
        var selected = rows.Where(row => row.IsSelected).ToArray();
        return Assert.Single(selected);
    }

    [Fact]
    public void BeforeDiscovery_TheTopLevelFoldersAreShown_AndOnlyInboxOffersAnArrow()
    {
        var sidebar = Sidebar();

        var rows = sidebar.Rows(FolderCatalog.Inbox.Key);

        Assert.Equal(FolderCatalog.All.Select(folder => folder.Key), rows.Select(row => row.Folder.Key));
        Assert.All(rows, row => Assert.Equal(0, row.Depth));
        Assert.All(rows, row => Assert.False(row.IsChild));

        var expandableRows = rows.Where(row => row.IsExpandable).ToArray();
        var expandable = Assert.Single(expandableRows);
        Assert.Equal(FolderCatalog.Inbox.Key, expandable.Folder.Key);
        Assert.False(expandable.IsExpanded);
        Assert.Empty(sidebar.Children);
        Assert.False(sidebar.Expanded);
    }

    [Fact]
    public void ExpandingInbox_ShowsTheCustomFoldersIndentedDirectlyBelowIt()
    {
        var sidebar = Expanded(Customers, Projects);

        var rows = sidebar.Rows(FolderCatalog.Inbox.Key);

        Assert.Equal(
            [FolderCatalog.Inbox.Key, CustomersKey, ProjectsKey, "sent", "drafts", "deleted", "junk", "archive"],
            rows.Select(row => row.Folder.Key));

        var children = rows.Where(row => row.IsChild).ToArray();
        Assert.Equal(2, children.Length);
        Assert.All(children, row => Assert.Equal(1, row.Depth));
        Assert.All(children, row => Assert.False(row.IsExpandable));
    }

    [Fact]
    public void ExpandingInbox_DoesNotChangeTheSelection()
    {
        var sidebar = Sidebar();
        Assert.Equal(FolderCatalog.Inbox.Key, Selected(sidebar.Rows(FolderCatalog.Inbox.Key)).Folder.Key);

        sidebar.Expand();
        sidebar.BeginChildrenLoad();
        sidebar.ApplyChildren([Customers]);

        Assert.True(sidebar.Expanded);
        Assert.Equal(FolderCatalog.Inbox.Key, Selected(sidebar.Rows(FolderCatalog.Inbox.Key)).Folder.Key);
    }

    [Fact]
    public void Collapsing_HidesTheChildren_AndKeepsTheSelection()
    {
        var sidebar = Expanded(Customers, Projects);

        sidebar.Collapse();

        var rows = sidebar.Rows(FolderCatalog.Inbox.Key);
        Assert.DoesNotContain(rows, row => row.IsChild);
        Assert.Equal(FolderCatalog.Inbox.Key, Selected(rows).Folder.Key);
        Assert.True(sidebar.CanExpand); // the arrow stays: the folders are still there
    }

    [Fact]
    public void SelectingACustomFolder_SelectsIt_DeselectsInbox_AndKeepsItsParentOpen()
    {
        var sidebar = Expanded(Customers, Projects);

        sidebar.Select(ProjectsKey);

        var rows = sidebar.Rows(ProjectsKey);
        Assert.True(sidebar.Expanded);
        var selected = Selected(rows);
        Assert.Equal(ProjectsKey, selected.Folder.Key);
        Assert.True(selected.IsChild);
        Assert.DoesNotContain(rows, row => row.IsSelected && row.Folder.Key == FolderCatalog.Inbox.Key);
    }

    [Fact]
    public void SelectingACustomFolder_WhileCollapsed_OpensItsParent()
    {
        var sidebar = Expanded(Customers);

        sidebar.Collapse();
        sidebar.Select(CustomersKey);

        Assert.True(sidebar.Expanded);
        Assert.Contains(sidebar.Rows(CustomersKey), row => row.IsChild && row.IsSelected);
    }

    [Fact]
    public void SelectingATopLevelFolder_DoesNotTouchTheHierarchy()
    {
        var sidebar = Expanded(Customers);

        sidebar.Select("deleted");

        Assert.True(sidebar.Expanded);
        Assert.Equal("deleted", Selected(sidebar.Rows("deleted")).Folder.Key);
    }

    [Fact]
    public void TwoCustomFoldersWithTheSameName_StayDistinct()
    {
        var first = Child("folder:4142", "Customers");
        var second = Child("folder:4344", "Customers");
        var sidebar = Expanded(first, second);

        var rows = sidebar.Rows(second.Key);

        var sameName = rows.Where(row => row.IsChild && row.Folder.Label == "Customers").ToArray();
        Assert.Equal(2, sameName.Length);
        Assert.Equal(second.Key, Selected(rows).Folder.Key);
        Assert.NotEqual(sameName[0].Folder.Key, sameName[1].Folder.Key);
    }

    [Fact]
    public void AnEmptyFolder_RemovesTheArrow_AndCollapsesTheChildList()
    {
        var sidebar = Expanded();

        Assert.True(sidebar.ChildrenLoaded);
        Assert.False(sidebar.CanExpand);
        Assert.False(sidebar.Expanded);

        var rows = sidebar.Rows(FolderCatalog.Inbox.Key);
        Assert.DoesNotContain(rows, row => row.IsExpandable);
        Assert.DoesNotContain(rows, row => row.IsChild);
        Assert.Equal(FolderCatalog.All.Select(folder => folder.Key), rows.Select(row => row.Folder.Key));
    }

    [Fact]
    public void FailedDiscovery_KeepsEveryTopLevelFolderUsable_AndOffersARetry()
    {
        var sidebar = Sidebar();
        sidebar.Expand();
        sidebar.BeginChildrenLoad();

        sidebar.FailChildrenLoad("Exchange connection failed. The server may be unreachable.");

        Assert.Equal("Exchange connection failed. The server may be unreachable.", sidebar.ChildrenError);
        Assert.False(sidebar.ChildrenLoading);
        Assert.True(sidebar.Expanded);   // the message is visible where the user asked for the folders
        Assert.True(sidebar.CanExpand);  // ... and the arrow is still there to retry
        Assert.Empty(sidebar.Children);

        // The mail client keeps working: Inbox and every other top-level folder stays selectable.
        var rows = sidebar.Rows("sent");
        Assert.Equal(FolderCatalog.All.Select(folder => folder.Key), rows.Select(row => row.Folder.Key));
        Assert.Equal("sent", Selected(rows).Folder.Key);
        Assert.DoesNotContain(rows, row => row.IsChild);
    }

    [Fact]
    public void RetryingAfterAFailure_AppliesTheFoldersAndClearsTheError()
    {
        var sidebar = Sidebar();
        sidebar.Expand();
        sidebar.BeginChildrenLoad();
        sidebar.FailChildrenLoad("Something unexpected went wrong. Please try again.");

        sidebar.BeginChildrenLoad();
        Assert.Null(sidebar.ChildrenError);
        Assert.True(sidebar.ChildrenLoading);

        sidebar.ApplyChildren([Customers]);

        Assert.Null(sidebar.ChildrenError);
        Assert.False(sidebar.ChildrenLoading);
        Assert.True(sidebar.CanExpand);
        Assert.Contains(sidebar.Rows(CustomersKey), row => row.Folder.Key == CustomersKey && row.IsChild);
    }

    [Fact]
    public void Reset_ForgetsTheHierarchy_WhenTheMailboxChanged()
    {
        var sidebar = Expanded(Customers);

        sidebar.Reset();

        Assert.Empty(sidebar.Children);
        Assert.False(sidebar.Expanded);
        Assert.False(sidebar.ChildrenLoaded);
        Assert.False(sidebar.ChildrenLoading);
        Assert.Null(sidebar.ChildrenError);
        Assert.True(sidebar.CanExpand);
        Assert.False(sidebar.IsChild(CustomersKey));
        Assert.Equal(
            FolderCatalog.All.Select(folder => folder.Key),
            sidebar.Rows(FolderCatalog.Inbox.Key).Select(row => row.Folder.Key));
    }

    [Fact]
    public void ADiscoveredFolderWithoutAName_StillRendersARow()
    {
        var folder = FolderInfo.FromMailFolder(new MailFolder
        {
            Id = CustomersKey,
            DisplayName = "   ",
            ParentId = FolderCatalog.Inbox.Key,
        });

        Assert.Equal(CustomersKey, folder.Key);
        Assert.Equal("(unnamed folder)", folder.Label);
        Assert.Equal(MailFolderTypes.Custom, folder.WellKnownType);
        Assert.False(folder.ShowsRecipients);
    }

    [Fact]
    public void OnlySentAndDrafts_ReadTheirRowsByRecipient()
    {
        static FolderInfo Root(string key) => FolderCatalog.All.Single(folder => folder.Key == key);

        Assert.True(Root("sent").ShowsRecipients);
        Assert.True(Root("drafts").ShowsRecipients);
        Assert.False(Root("inbox").ShowsRecipients);
        Assert.False(Root("deleted").ShowsRecipients);

        // A custom folder carries no well-known type, so its rows are read by sender, like Inbox.
        Assert.False(Customers.ShowsRecipients);
    }
}
