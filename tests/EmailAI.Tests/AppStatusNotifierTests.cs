using EmailAI.Api.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailAI.Tests;

/// <summary>
/// The in-process "configuration changed" fan-out. It is what makes the header status and the
/// mail page update the moment a save in Settings succeeds, so it has to be robust: every
/// subscriber hears about it, one broken circuit can never break the writer or the others, and
/// unsubscribing (a torn-down circuit) is always safe.
/// </summary>
public sealed class AppStatusNotifierTests
{
    [Fact]
    public void NotifyChanged_ReachesEverySubscriber_AndIncrementsTheRevision()
    {
        var notifier = Create();
        var seen = new List<AppStatusChange>();
        notifier.Changed += seen.Add;
        notifier.Changed += change => seen.Add(change);

        notifier.NotifyChanged(AppStatusArea.Ai, "settings-page.ai.save");

        Assert.Equal(2, seen.Count);
        Assert.All(seen, change => Assert.Equal(AppStatusArea.Ai, change.Area));
        Assert.All(seen, change => Assert.Equal("settings-page.ai.save", change.Source));
        Assert.Equal(1, notifier.Revision);
    }

    [Fact]
    public void NotifyChanged_ForwardsEveryArea()
    {
        var notifier = Create();
        var seen = new List<AppStatusArea>();
        notifier.Changed += change => seen.Add(change.Area);

        notifier.NotifyChanged(AppStatusArea.Ai, "a");
        notifier.NotifyChanged(AppStatusArea.Exchange, "b");
        notifier.NotifyChanged(AppStatusArea.All, "c");

        Assert.Equal([AppStatusArea.Ai, AppStatusArea.Exchange, AppStatusArea.All], seen);
        Assert.Equal(3, notifier.Revision);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery_AndRemovesTheSubscriber()
    {
        var notifier = Create();
        var received = 0;
        Action<AppStatusChange> handler = _ => received++;

        notifier.Changed += handler;
        Assert.Equal(1, notifier.SubscriberCount);

        notifier.NotifyChanged(AppStatusArea.Ai, "first");
        notifier.Changed -= handler;
        notifier.NotifyChanged(AppStatusArea.Ai, "second");

        Assert.Equal(1, received);
        Assert.Equal(0, notifier.SubscriberCount);
    }

    [Fact]
    public void AThrowingSubscriber_DoesNotPreventTheOthers()
    {
        var notifier = Create();
        var reached = false;
        notifier.Changed += _ => throw new InvalidOperationException("circuit is gone");
        notifier.Changed += _ => reached = true;

        var exception = Record.Exception(() => notifier.NotifyChanged(AppStatusArea.Exchange, "save"));

        Assert.Null(exception);
        Assert.True(reached);
        Assert.Equal(1, notifier.Revision);
    }

    [Fact]
    public void NotifyChanged_WithoutSubscribers_IsSafe_AndStillCounts()
    {
        var notifier = Create();

        notifier.NotifyChanged(new AppStatusChange(AppStatusArea.All, "test"));

        Assert.Equal(0, notifier.SubscriberCount);
        Assert.Equal(1, notifier.Revision);
    }

    private static AppStatusNotifier Create()
        => new(NullLogger<AppStatusNotifier>.Instance);
}
