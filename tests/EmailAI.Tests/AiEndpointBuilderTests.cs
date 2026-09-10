using EmailAI.Application.AI;
using EmailAI.Infrastructure.AI;

namespace EmailAI.Tests;

/// <summary>
/// Endpoint construction for the generic OpenAI-compatible provider: the base URL is the
/// provider's API root (e.g. "https://api.openai.com/v1") and the client appends exactly
/// "/chat/completions" (or "/models") - never a duplicated "/v1/v1" and never a doubled
/// "/chat/completions/chat/completions" or "/v1//chat/completions".
/// </summary>
public class AiEndpointBuilderTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com", "https://api.openai.com/chat/completions")]
    [InlineData("https://provider.test/", "https://provider.test/chat/completions")]
    [InlineData("https://company-ai.example.com/v1", "https://company-ai.example.com/v1/chat/completions")]
    [InlineData("https://proxy.example/llm/v1", "https://proxy.example/llm/v1/chat/completions")]
    [InlineData("https://host/v1/chat/completions", "https://host/v1/chat/completions")]
    [InlineData("https://host/v1/chat/completions/", "https://host/v1/chat/completions")]
    [InlineData("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080/v1/chat/completions")]
    public void ChatCompletions_AppendsExactlyOnce_AndNormalisesTrailingSlash(string baseUrl, string expected)
    {
        var uri = AiEndpointBuilder.ChatCompletions(baseUrl);
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/models")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/models")]
    [InlineData("https://api.openai.com", "https://api.openai.com/models")]
    [InlineData("https://host/models", "https://host/models")]
    [InlineData("https://host/models/", "https://host/models")]
    [InlineData("https://host/v1/models", "https://host/v1/models")]
    public void Models_AppendsExactlyOnce_AndNormalisesTrailingSlash(string baseUrl, string expected)
    {
        var uri = AiEndpointBuilder.Models(baseUrl);
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://provider.test/v1")]
    [InlineData("file:///tmp/socket")]
    public void Resolve_ThrowsInvalidConfiguration_ForUnusableBaseUrls(string baseUrl)
    {
        var exception = Assert.Throws<AiException>(() => AiEndpointBuilder.ChatCompletions(baseUrl));
        Assert.Equal(AiErrorKind.InvalidConfiguration, exception.Kind);
    }
}
