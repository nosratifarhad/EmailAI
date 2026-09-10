using System.Net;
using System.Net.Http;
using EmailAI.Application.Exceptions;
using Microsoft.Exchange.WebServices.Data;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// Converts the raw exceptions the EWS fork throws (response errors, transport
/// failures, protocol faults) into the single typed failure the application
/// understands: <see cref="ExchangeMailException"/>. Classification is driven by
/// <see cref="ServiceResponseException.ErrorCode"/> whenever the server answered
/// with a SOAP fault; transport/protocol exceptions fall back to heuristic
/// classification (auth/timeout hints in message text, HTTP status codes).
/// </summary>
internal static class EwsErrorClassifier
{
    public static ExchangeMailException Categorize(Exception exception, string operation)
    {
        if (exception is ExchangeMailException alreadyTyped)
        {
            return alreadyTyped;
        }

        var detail = DetailOf(exception);
        var message = $"Exchange operation '{operation}' failed. {detail}";

        return new ExchangeMailException(ToKind(exception), message, exception);
    }

    /// <summary>Short, user-safe description per failure kind (used by the health probe).</summary>
    public static string SafeText(ExchangeMailErrorKind kind) => kind switch
    {
        ExchangeMailErrorKind.Configuration => "Exchange is not configured.",
        ExchangeMailErrorKind.Authentication => "Exchange authentication failed.",
        ExchangeMailErrorKind.Connectivity => "Exchange connection failed; the server may be unreachable.",
        ExchangeMailErrorKind.NotFound => "The requested item could not be found.",
        ExchangeMailErrorKind.MailboxError => "Exchange returned an error while processing the mailbox.",
        ExchangeMailErrorKind.SendFailed => "The message could not be sent.",
        ExchangeMailErrorKind.Timeout => "Exchange did not respond in time.",
        _ => "An unexpected Exchange error occurred.",
    };

    private static ExchangeMailErrorKind ToKind(Exception exception)
    {
        if (exception is ServiceResponseException response)
        {
            return FromServiceError(response.ErrorCode);
        }

        // Order matters: ServerBusyException is a ServiceRemoteException subclass.
        if (exception is ServerBusyException)
        {
            return ExchangeMailErrorKind.MailboxError;
        }

        return exception switch
        {
            ServiceRequestException or WebException or HttpRequestException => FromTransport(exception),
            ServiceRemoteException => FromRemoteText(exception),
            ServiceLocalException or PropertyException or ServiceValidationException
                or ServiceVersionException or ServiceXmlDeserializationException
                or ServiceXmlSerializationException or TimeZoneConversionException
                or UpdateInboxRulesException => FromLocalText(exception),
            _ => FromRemoteText(exception),
        };
    }

    private static ExchangeMailErrorKind FromServiceError(ServiceError error) => error switch
    {
        ServiceError.ErrorItemNotFound or ServiceError.ErrorFolderNotFound => ExchangeMailErrorKind.NotFound,

        ServiceError.ErrorAccessDenied
            or ServiceError.ErrorImpersonateUserDenied
            or ServiceError.ErrorImpersonationFailed
            or ServiceError.ErrorAccountDisabled
            or ServiceError.ErrorSendAsDenied
            or ServiceError.ErrorNonExistentMailbox => ExchangeMailErrorKind.Authentication,

        ServiceError.ErrorTimeoutExpired => ExchangeMailErrorKind.Timeout,
        ServiceError.ErrorServerBusy => ExchangeMailErrorKind.MailboxError,
        _ => ExchangeMailErrorKind.MailboxError,
    };

    /// <summary>Transport-level failures (socket, DNS, HTTP protocol errors, proxy).</summary>
    private static ExchangeMailErrorKind FromTransport(Exception exception)
    {
        // Deepest message wins: a wrapped WebException carries the useful hint.
        var (kind, found) = FromHttpStatus(StatusCodeOf(exception));
        if (found)
        {
            return kind;
        }

        var text = DetailOf(exception).ToLowerInvariant();
        if (text.Contains("timed out") || text.Contains("timeout", StringComparison.Ordinal))
        {
            return ExchangeMailErrorKind.Timeout;
        }

        return ContainsAuthHint(text)
            ? ExchangeMailErrorKind.Authentication
            : ExchangeMailErrorKind.Connectivity;
    }

    /// <summary>Protocol-level errors that do not carry a typed ErrorCode.</summary>
    private static ExchangeMailErrorKind FromRemoteText(Exception exception)
    {
        var text = DetailOf(exception).ToLowerInvariant();
        if (text.Contains("timed out") || text.Contains("timeout", StringComparison.Ordinal))
        {
            return ExchangeMailErrorKind.Timeout;
        }

        return ContainsAuthHint(text)
            ? ExchangeMailErrorKind.Authentication
            : ExchangeMailErrorKind.MailboxError;
    }

    /// <summary>Client-side state errors from the EWS library itself (not server faults).</summary>
    private static ExchangeMailErrorKind FromLocalText(Exception exception)
    {
        var text = DetailOf(exception).ToLowerInvariant();
        if (text.Contains("timed out") || text.Contains("timeout", StringComparison.Ordinal))
        {
            return ExchangeMailErrorKind.Timeout;
        }

        return ExchangeMailErrorKind.MailboxError;
    }

    private static (ExchangeMailErrorKind Kind, bool Found) FromHttpStatus(HttpStatusCode? status) =>
        status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (ExchangeMailErrorKind.Authentication, true),
            HttpStatusCode.NotFound => (ExchangeMailErrorKind.NotFound, true),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => (ExchangeMailErrorKind.Timeout, true),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.BadGateway or HttpStatusCode.InternalServerError
                => (ExchangeMailErrorKind.MailboxError, true),
            _ => (ExchangeMailErrorKind.Connectivity, false),
        };

    private static HttpStatusCode? StatusCodeOf(Exception exception)
    {
        switch (exception)
        {
            case HttpRequestException http when http.StatusCode is not null:
                return http.StatusCode;
            case WebException web when web.Response is HttpWebResponse webResponse:
                return webResponse.StatusCode;
            case ServiceRequestException when exception.InnerException is not null:
                return StatusCodeOf(exception.InnerException);
            default:
                return null;
        }
    }

    private static bool ContainsAuthHint(string text) =>
        text.Contains("401", StringComparison.Ordinal)
        || text.Contains("403", StringComparison.Ordinal)
        || text.Contains("unauthori", StringComparison.Ordinal)
        || text.Contains("access denied", StringComparison.Ordinal)
        || text.Contains("logon failure", StringComparison.Ordinal)
        || text.Contains("impersonat", StringComparison.Ordinal);

    internal static string DetailOf(Exception exception)
    {
        if (exception is ServiceResponseException response)
        {
            var serverText = response.Response?.ErrorMessage;
            if (!string.IsNullOrWhiteSpace(serverText))
            {
                return $"ErrorCode={response.ErrorCode}; {serverText}";
            }
        }

        return exception.Message;
    }
}
