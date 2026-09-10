using EmailAI.Application.AI;
using EmailAI.Application.Exchange;

namespace EmailAI.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () =>
                Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
            .WithName("Health")
            .WithTags("health");

        app.MapGet("/health/exchange", async (IExchangeMailService mail, CancellationToken ct) =>
            {
                var health = await mail.CheckHealthAsync(ct);
                return health.IsHealthy
                    ? Results.Ok(new { status = "healthy", latencyMs = health.LatencyMs })
                    : Results.Json(
                        new { status = "unhealthy", error = health.Error },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("HealthExchange")
            .WithTags("health")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // AI status is reported WITHOUT failing the endpoint: /health (application
        // liveness) stays 200 whether or not the configured AI provider is reachable.
        // The probe is a lightweight GET {BaseUrl}/models - never a real prompt, and it
        // is only issued when AI is actually configured.
        //
        // "not_configured"        - no effective base URL/model (administrator must set
        //   AI_BASE_URL/AI_MODEL or the desktop user must configure the provider in
        //   Settings), or the per-user secure store holds no key yet (desktop user must
        //   add it in Settings). The key itself is never present in the response.
        // "connected"             - probe against the configured provider succeeded.
        // "authentication_failed" - the provider rejected the request (HTTP 401/403);
        //   the API key is wrong/missing. The key is never echoed.
        // "unavailable"           - provider unreachable or otherwise failing (sanitized
        //   error text only).
        app.MapGet("/health/ai", async (
                IAiClient aiClient,
                IAiCredentialService credentials,
                CancellationToken ct) =>
            {
                var status = await credentials.GetStatusAsync(ct);
                if (!status.Configured)
                {
                    return Results.Ok(new
                    {
                        status = "not_configured",
                        error = "AI is not configured. Configure your AI provider in Settings " +
                                "or set AI_BASE_URL and AI_MODEL.",
                    });
                }

                if (status.UserManaged && !status.HasApiKey)
                {
                    return Results.Ok(new
                    {
                        status = "not_configured",
                        error = "AI is not configured. Add your AI API key in Settings.",
                    });
                }

                var health = await aiClient.ProbeAsync(ct);
                if (health.IsAvailable)
                {
                    return Results.Ok(new { status = "connected", latencyMs = health.LatencyMs });
                }

                return health.ErrorKind == AiErrorKind.Authentication
                    ? Results.Ok(new
                    {
                        status = "authentication_failed",
                        error = "The AI provider rejected the connection. Check the configured API key.",
                    })
                    : Results.Ok(new { status = "unavailable", error = health.Error });
            })
            .WithName("HealthAi")
            .WithTags("health");
    }
}
