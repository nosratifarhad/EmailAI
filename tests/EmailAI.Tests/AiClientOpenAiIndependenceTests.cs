using System.Net;
using System.Text.Json;
using EmailAI.Application.AI;
using EmailAI.Domain.AI;
using EmailAI.Infrastructure.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailAI.Tests;

/// <summary>
/// Provider-independence and secret-hygiene guarantees for the OpenAI-compatible
/// client: the configured Base URL/Model/API key are the only provider inputs, no
/// vendor (OpenAI or otherwise) is assumed, and the API key can never leak into a
/// request body, an exception, a log line or a returned value.
/// </summary>
public class AiClientOpenAiIndependenceTests
{
    private const string SampleCompletion = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "model": "configured-model",
          "choices": [
            {
              "index": 0,
              "message": { "role": "assistant", "content": "The customer wants a refund." },
              "finish_reason": "stop"
            }
          ]
        }
        """;

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://some-other-provider.example.com/v1")]
    [InlineData("https://llm.corp.example/gateway/v1/")]
    [InlineData("https://ai.internal.example.com/openai-compatible/v1")]
    [InlineData("http://127.0.0.1:8321/v1")]
    public async Task CompleteAsync_AcceptsAnyOpenAiCompatibleBaseUrl(string baseUrl)
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(baseUrl: baseUrl, model: "configured-model"));

        await client.CompleteAsync(new AiChatRequest([]), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.StartsWith(baseUrl.TrimEnd('/') + "/chat/completions", request.Url.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/v1", request.Url.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("//chat/completions", request.Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_OpenAiIsNotRequired_AnyProviderAndModelWork()
    {
        const string baseUrl = "https://llm.internal.example.com/gateway/v1";
        const string model = "internal-model-7b";
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(baseUrl: baseUrl, apiKey: "corp-key", model: model));

        var completion = await client.CompleteAsync(
            new AiChatRequest([new AiChatMessage(AiChatRole.User, "Hi")]),
            CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal(new Uri(baseUrl + "/chat/completions"), request.Url);
        Assert.Equal("Bearer corp-key", request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
        Assert.NotNull(completion.Content);
        Assert.NotEqual(string.Empty, completion.Content);
    }

    [Fact]
    public async Task CompleteAsync_SendsConfiguredModelAndKey_NeverInRequestBody()
    {
        const string apiKey = "sk-custom-secret-value";
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(HttpStatusCode.OK, SampleCompletion));
        var client = CreateClient(handler, TestOptions.Ai(apiKey: apiKey, model: "my-custom-model"));

        var completion = await client.CompleteAsync(
            new AiChatRequest([new AiChatMessage(AiChatRole.User, "Hi")]),
            CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("Bearer " + apiKey, request.Authorization);
        Assert.NotNull(request.Body);
        Assert.DoesNotContain(apiKey, request.Body, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("my-custom-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("The customer wants a refund.", completion.Content);
    }

    [Fact]
    public async Task CompleteAsync_AuthenticationFailure_NeverExposesKeyInExceptionOrLogs()
    {
        const string apiKey = "sk-top-secret-do-not-leak";
        var logger = new RecordingLogger();
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = CreateClient(handler, TestOptions.Ai(apiKey: apiKey), logger);

        var exception = await Assert.ThrowsAsync<AiException>(() =>
            client.CompleteAsync(new AiChatRequest([]), CancellationToken.None));

        Assert.Equal(AiErrorKind.Authentication, exception.Kind);
        Assert.DoesNotContain(apiKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, logger.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_ConnectivityFailure_NeverExposesKeyInExceptionOrLogs()
    {
        const string apiKey = "sk-top-secret-do-not-leak";
        var logger = new RecordingLogger();
        var handler = new ThrowingHttpHandler(new HttpRequestException("connection refused"));
        var client = CreateClient(handler, TestOptions.Ai(apiKey: apiKey), logger);

        var exception = await Assert.ThrowsAsync<AiException>(() =>
            client.CompleteAsync(new AiChatRequest([]), CancellationToken.None));

        Assert.Equal(AiErrorKind.Unavailable, exception.Kind);
        Assert.DoesNotContain(apiKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, logger.AllText, StringComparison.Ordinal);
    }

    private static OpenAiCompatClient CreateClient(
        HttpMessageHandler handler,
        AiOptions options,
        ILogger<OpenAiCompatClient>? logger = null)
        => new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new StaticAiConfigurationProvider(options),
            new StaticAiCredentialProvider(options.ApiKey),
            logger ?? NullLogger<OpenAiCompatClient>.Instance);

    private sealed class ThrowingHttpHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    /// <summary>In-memory ILogger that captures every message so tests can assert a
    /// secret never reached a log line (message text or exception payload).</summary>
    internal sealed class RecordingLogger : ILogger<OpenAiCompatClient>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            _lines.Enqueue(text);
            if (exception is not null)
            {
                _lines.Enqueue(exception.ToString());
            }
        }

        public string AllText => string.Join('\n', _lines);
    }
}

