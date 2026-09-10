using EmailAI.Application.AI;

namespace EmailAI.Infrastructure.AI;

/// <summary>
/// Builds OpenAI-compatible endpoint URLs from a user-configured AI base URL.
///
/// The base URL is exactly what the user enters in Settings or as AI_BASE_URL and is
/// used verbatim (no vendor-specific segments such as "/v1" are force-appended - the
/// user is expected to include the API root, e.g. "https://api.openai.com/v1"):
///   https://api.openai.com/v1                     -> .../v1/chat/completions
///   https://api.openai.com/v1/                    -> .../v1/chat/completions
///   https://host/company-ai/v1                    -> .../company-ai/v1/chat/completions
///   https://host/v1/chat/completions              -> unchanged (full URL was given)
///
/// Trailing slashes are normalized so a request can never become
/// ".../chat/completions/chat/completions" or "/v1//chat/completions".
/// </summary>
public static class AiEndpointBuilder
{
    public static Uri ChatCompletions(string baseUrl) => Resolve(baseUrl, "/chat/completions");

    public static Uri Models(string baseUrl) => Resolve(baseUrl, "/models");

    private static Uri Resolve(string baseUrl, string tailSegment)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new AiException(
                AiErrorKind.InvalidConfiguration,
                "The configured AI base URL must be an absolute http(s) URL " +
                "(for example https://api.openai.com/v1 or http://localhost:4000/v1).");
        }

        // Trim the trailing slash (also normalizes a full endpoint URL that already ends
        // with the requested segment) and append the segment exactly once.
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith(tailSegment, StringComparison.OrdinalIgnoreCase))
        {
            path += tailSegment;
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = path,
        };

        return builder.Uri;
    }
}
