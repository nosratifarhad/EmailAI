using System.Text.Json;
using EmailAI.Application.AI;

namespace EmailAI.Api.Web;

/// <summary>
/// Resolves the requested AI output language from <see cref="AiOperationRequest"/>.
///
/// Accepted forms (the wire contract is deliberately forgiving, because the language name is
/// what the API documents and what a hand-written client sends):
///   * <c>{"language":"Persian"}</c> - the documented name, any casing;
///   * <c>{"language":"2"}</c> or <c>{"language":2}</c> - the numeric value;
///   * omitted / null / blank - Auto (follow the source email language).
///
/// Anything else is rejected with a message the caller can show: the alternative would be the
/// framework's empty 400, which tells a user nothing. Nothing here depends on the email content,
/// so a language can never be influenced by the message being processed.
/// </summary>
public static class AiLanguageRequest
{
    /// <summary>Message used when the caller supplied a name that is not a supported language.</summary>
    public const string UnsupportedNameFormat =
        "The requested response language '{0}' is not supported. Use Auto, English or Persian.";

    /// <summary>Message used when the caller supplied an unsupported numeric value.</summary>
    public const string UnsupportedValueFormat =
        "The requested response language value {0} is not supported. Use Auto (0), English (1) or Persian (2).";

    /// <summary>Message used for a token that is neither a name nor a number.</summary>
    public const string UnsupportedTokenMessage =
        "The requested response language must be a name (Auto, English, Persian) or its numeric value.";

    /// <summary>
    /// Reads the requested language. Returns false (with a user-facing <paramref name="error"/>)
    /// only when the caller supplied a value that is not a language - a missing value is Auto.
    /// </summary>
    public static bool TryResolve(
        AiOperationRequest? request,
        out AiLanguage language,
        out string? error)
    {
        language = AiLanguage.Auto;
        error = null;

        if (request?.Language is not { } token
            || token.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        switch (token.ValueKind)
        {
            case JsonValueKind.String:
                var name = token.GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    return true;
                }

                if (Enum.TryParse<AiLanguage>(name.Trim(), ignoreCase: true, out var parsed)
                    && Enum.IsDefined(typeof(AiLanguage), parsed))
                {
                    language = parsed;
                    return true;
                }

                error = string.Format(UnsupportedNameFormat, name.Trim());
                return false;

            case JsonValueKind.Number:
                if (token.TryGetInt32(out var number)
                    && Enum.IsDefined(typeof(AiLanguage), number))
                {
                    language = (AiLanguage)number;
                    return true;
                }

                error = string.Format(UnsupportedValueFormat, token.GetRawText());
                return false;

            default:
                error = UnsupportedTokenMessage;
                return false;
        }
    }
}
