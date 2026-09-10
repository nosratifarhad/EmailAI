using System.Text.Json;

namespace EmailAI.Api.Web;

/// <summary>
/// A non-success response from the EmailAI REST API. <see cref="Message"/> is always
/// a user-presentable sentence (never a stack trace); technical details stay in the
/// backend structured logs and are referenced by <see cref="TraceId"/>.
/// </summary>
public sealed class EmailApiException(int statusCode, string? errorCode, string message, string? traceId)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string? ErrorCode { get; } = errorCode;

    /// <summary>Backend trace identifier, shown in the UI so a problem can be reported.</summary>
    public string? TraceId { get; } = traceId;

    /// <summary>
    /// Builds an exception from an error response. The API uses two envelopes:
    ///   { "error": { "code", "message", "traceId" } }   (ExceptionHandlingMiddleware)
    ///   { "error": "plain text" }                       (inline Results.BadRequest)
    /// </summary>
    internal static async Task<EmailApiException> FromResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string? code = null;
        string? message = null;
        string? traceId = null;

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            body = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.Object)
                    {
                        code = ReadString(error, "code");
                        message = ReadString(error, "message");
                        traceId = ReadString(error, "traceId");
                    }
                    else if (error.ValueKind == JsonValueKind.String)
                    {
                        message = error.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON (proxy error page etc.) - fall back to a generic message.
            }
        }

        message ??= status switch
        {
            400 => "The request was invalid.",
            401 => "Authentication is required.",
            403 => "Access was denied.",
            404 => "The requested message could not be found.",
            503 => "The service is temporarily unavailable.",
            _ => $"The request failed (HTTP {status}).",
        };

        return new EmailApiException(status, code, message, traceId);
    }

    private static string? ReadString(JsonElement parent, string propertyName)
    {
        if (parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }
}
