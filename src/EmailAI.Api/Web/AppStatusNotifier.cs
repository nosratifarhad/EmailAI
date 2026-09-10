namespace EmailAI.Api.Web;

/// <summary>Which part of the application changed state.</summary>
public enum AppStatusArea
{
    /// <summary>The AI provider configuration/credential state changed.</summary>
    Ai,

    /// <summary>The Exchange connection configuration changed.</summary>
    Exchange,

    /// <summary>Both changed (for example a Settings page save of both sections).</summary>
    All,
}

/// <summary>What changed, and where it came from (for logs/diagnostics).</summary>
/// <param name="Area">The affected area.</param>
/// <param name="Source">Short, secret-free description of the writer ("settings-page", ...).</param>
public sealed record AppStatusChange(AppStatusArea Area, string Source);

/// <summary>
/// In-process fan-out used to propagate a CONFIGURATION change to every live UI circuit, so
/// the header status (and anything else that reflects AI/Exchange readiness) updates
/// immediately instead of waiting for the next poll or a page reload.
///
/// It is deliberately tiny: a singleton holding a handler list plus a monotonic
/// <see cref="Revision"/> (useful for diagnostics and tests). Components subscribe on
/// initialization, marshal to their own dispatcher and unsubscribe on disposal. A handler
/// that throws (for example a circuit that is being torn down) can never break the writer or
/// the other subscribers.
/// </summary>
public sealed class AppStatusNotifier(ILogger<AppStatusNotifier> logger)
{
    private readonly object _gate = new();
    private readonly List<Action<AppStatusChange>> _handlers = [];

    /// <summary>Number of change notifications raised by this process (monotonic).</summary>
    public int Revision { get; private set; }

    /// <summary>Raised for every configuration change. Handlers run on the writer's thread.</summary>
    public event Action<AppStatusChange>? Changed
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                _handlers.Add(value);
            }
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                _handlers.Remove(value);
            }
        }
    }

    /// <summary>Number of subscribed components (diagnostics/tests).</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _handlers.Count;
            }
        }
    }

    /// <summary>Notifies every subscriber that the AI and/or Exchange configuration changed.</summary>
    public void NotifyChanged(AppStatusArea area, string source)
        => NotifyChanged(new AppStatusChange(area, source));

    /// <summary>Notifies every subscriber that a configuration changed.</summary>
    public void NotifyChanged(AppStatusChange change)
    {
        Action<AppStatusChange>[] handlers;
        int revision;

        lock (_gate)
        {
            Revision++;
            revision = Revision;
            handlers = [.. _handlers];
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(change);
            }
            catch (Exception exception)
            {
                // One unhealthy circuit must never stop the others from updating.
                logger.LogWarning(
                    "A status-change subscriber failed. area={Area} revision={Revision} reason={Reason}",
                    change.Area, revision, exception.GetType().Name);
            }
        }
    }
}
