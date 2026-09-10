using System.Net;
using System.Text.Json;
using EmailAI.Application.AI;
using EmailAI.Domain.AI;
using EmailAI.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailAI.Tests;

public class OpenAiCompatClientTests
{
    private const string SampleCompletion = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "model": "gpt-5",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "The customer wants a refund." },
              "finish_reason": "stop"
            }
          ]
        }
        """;

    // ------------------------------------------------------------------
    // Missing configuration (test requirement 1)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_MissingConfiguration_ThrowsNotConfigured_WithoutCallingHttp()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("HTTP must not be called."));
        var client = CreateClient(handler, TestOptions.Ai(baseUrl: null, model: null));

        var exception = await Assert.ThrowsAsync<AiException>(() =>
            client.CompleteAsync(new AiChatRequest([]), CancellationToken.None));

        Assert.Equal(AiErrorKind.NotConfigured, exception.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProbeAsync_MissingConfiguration_ThrowsNotConfigured_WithoutCallingHttp()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("HTTP must not be called."));
        var client = CreateClient(handler, TestOptions.Ai(baseUrl: null, apiKey: null, model: null));

        var exception = await Assert.ThrowsAsync<AiException>(() => client.ProbeAsync(CancellationToken.None));

        Assert.Equal(AiErrorKind.NotConfigured, exception.Kind);
        Assert.Empty(handler.Requests);
    }

    // ------------------------------------------------------------------
    // Endpoint construction (test requirement: POST {BaseUrl}/chat/completions)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_PostsToConfiguredBaseUrlChatCompletions()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(baseUrl: "https://api.openai.com/v1"));

        await client.CompleteAsync(new AiChatRequest([]), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.Equal(
            new Uri("https://api.openai.com/v1/chat/completions"),
            handler.Requests.Single().Url);
    }

    [Fact]
    public async Task CompleteAsync_BaseUrlWithTrailingSlash_IsNormalised_NoDoubledPath()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(baseUrl: "https://provider.test/v1/"));

        await client.CompleteAsync(new AiChatRequest([]), CancellationToken.None);

        Assert.Equal(
            new Uri("https://provider.test/v1/chat/completions"),
            handler.Requests.Single().Url);
    }

    // ------------------------------------------------------------------
    // Model selection (test requirement 3) + auth header (requirement 4)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_UsesConfiguredModel_WhenRequestDoesNotSpecifyOne()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(model: "configured-model"));

        await client.CompleteAsync(new AiChatRequest([new AiChatMessage(AiChatRole.User, "Hi")]), CancellationToken.None);

        Assert.Equal("configured-model", ReadJsonField(handler.Requests.Single().Body!, "model"));
    }

    [Fact]
    public async Task CompleteAsync_RequestModelOverride_WinsOverConfiguredModel()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(model: "configured-model"));

        await client.CompleteAsync(
            new AiChatRequest([new AiChatMessage(AiChatRole.User, "Hi")], Model: "override-model"),
            CancellationToken.None);

        Assert.Equal("override-model", ReadJsonField(handler.Requests.Single().Body!, "model"));
    }

    [Fact]
    public async Task CompleteAsync_SendsBearerApiKey_WhenConfigured()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(apiKey: "top-secret"));

        await client.CompleteAsync(new AiChatRequest([]), CancellationToken.None);

        Assert.Equal("Bearer top-secret", handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task CompleteAsync_OmitsAuthorizationHeader_WhenNoApiKeyConfigured()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(apiKey: null));

        await client.CompleteAsync(new AiChatRequest([]), CancellationToken.None);

        Assert.Null(handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task CompleteAsync_ResolvesKeyFromCredentialProvider_NotFromOptions()
    {
        // The options carry one key but the provider supplies another: the provider is
        // authoritative (it mirrors the secure-store-first policy). This is what makes
        // the credential abstractions effective - the wire request must never use the
        // stale configuration value when the secure store has a newer key.
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(
            handler,
            TestOptions.Ai(apiKey: "stale-config-key"),
            credentials: new StaticAiCredentialProvider("secure-store-key"));

        await client.CompleteAsync(new AiChatRequest([]), CancellationToken.None);

        Assert.Equal("Bearer secure-store-key", handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task CompleteAsync_ProviderReturningNull_OmitsHeader_EvenWhenOptionsCarryAKey()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(
            handler,
            TestOptions.Ai(apiKey: "config-key"),
            credentials: new StaticAiCredentialProvider(null));

        await client.CompleteAsync(new AiChatRequest([]), CancellationToken.None);

        Assert.Null(handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task ProbeAsync_ResolvesKeyFromCredentialProvider()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, """{ "data": [] }"""));
        var client = CreateClient(
            handler,
            TestOptions.Ai(apiKey: "stale-config-key"),
            credentials: new StaticAiCredentialProvider("secure-store-key"));

        await client.ProbeAsync(CancellationToken.None);

        Assert.Equal("Bearer secure-store-key", handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task CompleteAsync_RoleAndContentAreMapped_IntoTheWirePayload()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai());

        await client.CompleteAsync(
            new AiChatRequest(
            [
                new AiChatMessage(AiChatRole.System, "be safe"),
                new AiChatMessage(AiChatRole.User, "please summarize"),
            ]),
            CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Requests.Single().Body!);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("please summarize", messages[1].GetProperty("content").GetString());
    }


    // ------------------------------------------------------------------
    // Successful parsing (test requirement 5)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_ParsesStandardOpenAiResponse()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(model: "gpt-5"));

        var completion = await client.CompleteAsync(new AiChatRequest([]), CancellationToken.None);

        Assert.Equal("The customer wants a refund.", completion.Content);
        Assert.Equal("gpt-5", completion.Model);
        Assert.Equal("stop", completion.FinishReason);
    }

    // ------------------------------------------------------------------
    // Non-success HTTP (test requirement 6)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, AiErrorKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, AiErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, AiErrorKind.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, AiErrorKind.Unavailable)]
    public async Task CompleteAsync_MapsNonSuccessStatuses(HttpStatusCode status, AiErrorKind expectedKind)
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(status));
        var client = CreateClient(handler, TestOptions.Ai());

        var exception = await Assert.ThrowsAsync<AiException>(() =>
            client.CompleteAsync(new AiChatRequest([]), CancellationToken.None));

        Assert.Equal(expectedKind, exception.Kind);
    }

    // ------------------------------------------------------------------
    // Timeout (test requirement 7)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_TimesOut_WhenProviderNeverResponds()
    {
        var handler = new DelayHttpHandler(TimeSpan.FromSeconds(10));
        var client = CreateClient(handler, TestOptions.Ai(timeoutSeconds: 1));

        var exception = await Assert.ThrowsAsync<AiException>(() =>
            client.CompleteAsync(new AiChatRequest([]), CancellationToken.None));

        Assert.Equal(AiErrorKind.Timeout, exception.Kind);
    }

    [Fact]
    public async Task CompleteAsync_PropagatesCallerCancellation_WithoutMappingToTimeout()
    {
        var handler = new DelayHttpHandler(TimeSpan.FromSeconds(10));
        var client = CreateClient(handler, TestOptions.Ai(timeoutSeconds: 60));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CompleteAsync(new AiChatRequest([]), cts.Token));
    }

    // ------------------------------------------------------------------
    // Empty / malformed responses (test requirement 8)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "choices": [] }""")]
    [InlineData("""{ "choices": [ { "message": { "role": "assistant", "content": "" } } ] }""")]
    [InlineData("""{ "choices": [ { "message": { "role": "assistant", "content": "   " } } ] }""")]
    [InlineData("""{ "unexpected": true }""")]
    [InlineData("not json at all")]
    public async Task CompleteAsync_EmptyOrMalformedResponse_ThrowsInvalidResponse(string body)
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, body));
        var client = CreateClient(handler, TestOptions.Ai());

        var exception = await Assert.ThrowsAsync<AiException>(() =>
            client.CompleteAsync(new AiChatRequest([]), CancellationToken.None));

        Assert.Equal(AiErrorKind.InvalidResponse, exception.Kind);
    }



    // ------------------------------------------------------------------
    // Probe
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProbeAsync_Connected_WhenModelsEndpointSucceeds()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, """{ "data": [] }"""));
        var client = CreateClient(handler, TestOptions.Ai());

        var result = await client.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Null(result.Error);
        Assert.NotNull(result.LatencyMs);
        Assert.Equal(new Uri("https://provider.test/v1/models"), handler.Requests.Single().Url);
    }

    [Fact]
    public async Task ProbeAsync_Unavailable_WhenProviderReturnsError()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = CreateClient(handler, TestOptions.Ai());

        var result = await client.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("503", result.Error);
        Assert.Equal(AiErrorKind.Unavailable, result.ErrorKind);
    }

    [Fact]
    public async Task ProbeAsync_AuthenticationFailed_WhenProviderReturns401()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = CreateClient(handler, TestOptions.Ai());

        var result = await client.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("401", result.Error);
        Assert.Equal(AiErrorKind.Authentication, result.ErrorKind);
    }

    [Fact]
    public async Task ProbeAsync_Unreachable_ReturnsResult_WithoutThrowing()
    {
        var handler = new ThrowingHttpHandler(new HttpRequestException("connection refused"));
        var client = CreateClient(handler, TestOptions.Ai());

        var result = await client.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AiErrorKind.Unavailable, result.ErrorKind);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static OpenAiCompatClient CreateClient(
        HttpMessageHandler handler,
        AiOptions options,
        IAiCredentialProvider? credentials = null)
        => new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new StaticAiConfigurationProvider(options),
            credentials ?? new StaticAiCredentialProvider(options.ApiKey),
            NullLogger<OpenAiCompatClient>.Instance);

    private static string? ReadJsonField(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(name).GetString();
    }

    private sealed class DelayHttpHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingHttpHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}

