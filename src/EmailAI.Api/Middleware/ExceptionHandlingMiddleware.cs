using EmailAI.Application.AI;
using EmailAI.Application.Exceptions;
using EmailAI.Application.Settings;

namespace EmailAI.Api.Middleware;

/// <summary>
/// Centralised error handling. Maps typed Exchange failures to meaningful,
/// non-leaking HTTP responses; anything unexpected becomes a generic 500 with
/// the technical detail kept in the structured log.
/// </summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteErrorAsync(context, ex);
        }
    }

    private async Task WriteErrorAsync(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            logger.LogError(exception, "Exception thrown after the response started.");
            throw exception;
        }

        var (status, code, message) = Map(exception);

        logger.LogError(exception,
            "Request {Method} {Path} failed. code={Code} status={Status} trace={TraceId}",
            context.Request.Method, context.Request.Path, code, status, context.TraceIdentifier);

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code,
                message,
                traceId = context.TraceIdentifier,
            },
        });
    }

    private static (int Status, string Code, string Message) Map(Exception exception)
    {
        if (exception is ExchangeMailException mail)
        {
            return mail.Kind switch
            {
                ExchangeMailErrorKind.Configuration => (503, "exchange_not_configured",
                    "Exchange is not configured. Contact your administrator."),
                ExchangeMailErrorKind.Authentication => (502, "exchange_authentication_failed",
                    "Exchange authentication failed. Check the configured credentials."),
                ExchangeMailErrorKind.Connectivity => (503, "exchange_connection_failed",
                    "Exchange connection failed. The server may be unreachable."),
                ExchangeMailErrorKind.Timeout => (504, "exchange_timeout",
                    "Exchange did not respond in time."),
                ExchangeMailErrorKind.BadRequest => (400, "bad_request",
                    mail.Message),
                ExchangeMailErrorKind.NotFound => (404, "message_not_found",
                    "The requested message could not be found."),
                ExchangeMailErrorKind.FolderNotFound => (404, "folder_not_found",
                    "The requested folder could not be found or is no longer available."),
                ExchangeMailErrorKind.SendFailed => (502, "message_send_failed",
                    "The message could not be sent. Nothing was sent."),
                ExchangeMailErrorKind.MailboxError => (502, "exchange_mailbox_error",
                    "Exchange returned an error while processing the mailbox."),
                _ => (500, "internal_error", "An unexpected error occurred."),
            };
        }

        if (exception is AiException ai)
        {
            return ai.Kind switch
            {
                AiErrorKind.NotConfigured => (503, "ai_not_configured",
                    "AI is not configured. Contact your administrator."),
                AiErrorKind.InvalidConfiguration => (500, "ai_invalid_configuration",
                    "The AI service is misconfigured. Contact your administrator."),
                AiErrorKind.Authentication => (502, "ai_authentication_failed",
                    "The AI provider rejected the request. Check the configured API key (Settings or AI_API_KEY)."),
                AiErrorKind.RateLimited => (429, "ai_rate_limited",
                    "The AI provider is rate-limiting requests. Try again shortly."),
                AiErrorKind.Unavailable => (503, "ai_unavailable",
                    "The AI service is unavailable right now. Try again later."),
                AiErrorKind.Timeout => (504, "ai_timeout",
                    "The AI service did not respond in time. Try again."),
                AiErrorKind.InvalidResponse => (502, "ai_invalid_response",
                    "The AI service returned an unexpected or empty response."),
                _ => (500, "internal_error", "An unexpected error occurred."),
            };
        }

        if (exception is AiCredentialStoreException)
        {
            return (500, "ai_credential_store_failed",
                "The secure credential store could not be used. Sign in to Windows and try again.");
        }

        if (exception is SecretStoreException)
        {
            return (500, "credential_store_failed",
                "The secure credential store could not be used. Sign in to Windows and try again.");
        }

        return (500, "internal_error", "An unexpected error occurred.");
    }
}
