namespace EmailAI.Api.Web;

/// <summary>Shape of GET /health (200).</summary>
public sealed class ServiceHealth
{
    public string? Status { get; init; }

    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>
/// Shape of GET /health/exchange. The same JSON is returned for the healthy (200)
/// and unhealthy (503) cases, so the client parses one shape for both.
/// </summary>
public sealed class ExchangeHealth
{
    public string? Status { get; init; }

    public long? LatencyMs { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Shape of GET /health/ai. Always HTTP 200 with the state in <see cref="Status"/>:
/// "connected" | "not_configured" | "unavailable".
/// </summary>
public sealed class AiHealth
{
    public string? Status { get; init; }

    public long? LatencyMs { get; init; }

    public string? Error { get; init; }
}
