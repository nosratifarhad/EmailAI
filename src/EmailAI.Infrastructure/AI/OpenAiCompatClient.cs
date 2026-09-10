using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmailAI.Application.AI;
using EmailAI.Domain.AI;
using Microsoft.Extensions.Logging;

namespace EmailAI.Infrastructure.AI;

/// <summary>
/// Server-side OpenAI-compatible chat client. Speaks the standard "chat/completions"
/// HTTP API to whatever OpenAI-compatible provider the user configured (OpenAI, or any
/// other provider/gateway that exposes the same API).
///
/// Responsibilities: resolve the EFFECTIVE provider settings (server configuration
/// merged with per-user Settings overrides) through <see cref="IAiConfigurationProvider"/>,
/// apply the configured model/timeout, resolve the API key at runtime through
/// <see cref="IAiCredentialProvider"/> (never from the browser), build the request URL
/// from the configured base URL, parse (or reject) the response, and translate every
/// failure into a typed <see cref="AiException"/>. No business prompts live here.
///
/// Secrets: the API key is resolved per request and sent only in the Authorization
/// header of the outbound request. It is never cached to disk, never logged, never
/// returned to callers, and never exposed to the browser. Email bodies are never
/// logged either - failures log status/URL/model only.
/// </summary>
public sealed class OpenAiCompatClient : IAiClient
{
    // The models probe is a connectivity check only; it must never wait as long as a
    // real chat completion.
    private const int ProbeTimeoutSeconds = 10;

    private readonly HttpClient _http;
    private readonly IAiConfigurationProvider _configuration;
    private readonly IAiCredentialProvider _credentials;
    private readonly ILogger<OpenAiCompatClient> _logger;

    public OpenAiCompatClient(
        HttpClient http,
        IAiConfigurationProvider configuration,
        IAiCredentialProvider credentials,
        ILogger<OpenAiCompatClient> logger)
    {
        _http = http;
        _configuration = configuration;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<AiChatCompletion> CompleteAsync(
        AiChatRequest request,
        CancellationToken cancellationToken)
    {
        var options = await _configuration.GetEffectiveOptionsAsync(cancellationToken);
        EnsureConfigured(options);

        var model = string.IsNullOrWhiteSpace(request.Model) ? options.Model : request.Model!;
        var url = AiEndpointBuilder.ChatCompletions(options.BaseUrl);

        // Resolve the API key per request (never cached on disk). The secure store is
        // the authoritative source on Windows desktop; configuration/AI_API_KEY is the
        // server/CI fallback. Null/empty means the provider needs no key.
        var apiKey = await _credentials.GetApiKeyAsync(cancellationToken);

        using var timeoutCts = CreateTimeoutSource(options.TimeoutSeconds, cancellationToken);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);

        var auth = BuildAuth(apiKey);
        if (auth is not null)
        {
            httpRequest.Headers.Authorization = auth;
        }

        httpRequest.Content = JsonContent.Create(BuildPayload(model, request));

        try
        {
            using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseContentRead,
                timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                LogFailure(HttpMethod.Post, url, status, model);
                throw MapStatus(status);
            }

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var completion = ParseCompletion(json, model);
            _logger.LogInformation(
                "AI chat completion succeeded. model={Model} finish={FinishReason}",
                completion.Model ?? model, completion.FinishReason ?? "(unknown)");
            return completion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled (user navigated away / cancelled) - propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout token fired.
            throw new AiException(
                AiErrorKind.Timeout,
                $"The AI request did not respond within {options.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception,
                "AI chat completion failed at the transport level. url={Url} model={Model}",
                SafeUrl(url), model);
            throw new AiException(AiErrorKind.Unavailable, "The AI service could not be reached.");
        }
        catch (JsonException exception)
        {
            throw new AiException(
                AiErrorKind.InvalidResponse,
                "The AI service returned a malformed response.",
                exception);
        }
    }

    public async Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var options = await _configuration.GetEffectiveOptionsAsync(cancellationToken);
        EnsureConfigured(options);

        var url = AiEndpointBuilder.Models(options.BaseUrl);
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = CreateTimeoutSource(
            Math.Min(options.TimeoutSeconds, ProbeTimeoutSeconds),
            cancellationToken);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);

        var apiKey = await _credentials.GetApiKeyAsync(cancellationToken);
        var auth = BuildAuth(apiKey);
        if (auth is not null)
        {
            httpRequest.Headers.Authorization = auth;
        }

        try
        {
            using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "AI connectivity probe succeeded in {LatencyMs} ms.",
                    stopwatch.ElapsedMilliseconds);
                return new AiProbeResult(true, stopwatch.ElapsedMilliseconds, null);
            }

            var status = (int)response.StatusCode;
            LogFailure(HttpMethod.Get, url, status, options.Model);
            return new AiProbeResult(
                false,
                stopwatch.ElapsedMilliseconds,
                $"AI service responded with HTTP {status}.",
                ProbeStatus(status));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancelled
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new AiProbeResult(
                false,
                stopwatch.ElapsedMilliseconds,
                "AI connectivity probe timed out.",
                AiErrorKind.Timeout);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            _logger.LogWarning(exception,
                "AI connectivity probe failed at the transport level. url={Url}",
                SafeUrl(url));
            return new AiProbeResult(
                false,
                stopwatch.ElapsedMilliseconds,
                "AI service is unreachable.",
                AiErrorKind.Unavailable);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _logger.LogError(exception, "AI connectivity probe failed unexpectedly.");
            return new AiProbeResult(
                false,
                stopwatch.ElapsedMilliseconds,
                "AI status check failed.",
                AiErrorKind.Unavailable);
        }
    }


    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void EnsureConfigured(AiOptions options)
    {
        if (!options.IsConfigured)
        {
            throw new AiException(
                AiErrorKind.NotConfigured,
                "AI is not configured. Configure the AI provider in Settings or set the " +
                "AI_BASE_URL and AI_MODEL environment variables (see .env.example).");
        }
    }

    private static CancellationTokenSource CreateTimeoutSource(int timeoutSeconds, CancellationToken callerToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        return timeoutCts;
    }

    private static AuthenticationHeaderValue? BuildAuth(string? apiKey)
        => string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new AuthenticationHeaderValue("Bearer", apiKey);

    private static object BuildPayload(string model, AiChatRequest request)
    {
        var payload = new ChatPayload
        {
            Model = model,
            Messages = request.Messages
                .Select(message => new ChatMessagePayload
                {
                    Role = RoleName(message.Role),
                    Content = message.Content,
                })
                .ToArray(),
        };

        if (request.Temperature is { } temperature)
        {
            payload.Temperature = temperature;
        }

        return payload;
    }

    private static string RoleName(AiChatRole role) => role switch
    {
        AiChatRole.System => "system",
        AiChatRole.User => "user",
        AiChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static AiChatCompletion ParseCompletion(string json, string requestedModel)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new AiException(
                AiErrorKind.InvalidResponse,
                "The AI service response contained no choices.");
        }

        var firstChoice = choices[0];
        if (firstChoice.ValueKind != JsonValueKind.Object ||
            !firstChoice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            throw new AiException(
                AiErrorKind.InvalidResponse,
                "The AI service response contained no message content.");
        }

        var text = content.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new AiException(
                AiErrorKind.InvalidResponse,
                "The AI service returned an empty response.");
        }

        var model = TryReadString(root, "model") ?? requestedModel;
        var finishReason = firstChoice.TryGetProperty("finish_reason", out var finish) &&
                           finish.ValueKind == JsonValueKind.String
            ? finish.GetString()
            : null;

        return new AiChatCompletion(text, model, finishReason);
    }

    private static string? TryReadString(JsonElement parent, string propertyName)
    {
        if (parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }


    private static AiException MapStatus(int status) => status switch
    {
        401 or 403 => new AiException(
            AiErrorKind.Authentication,
            $"The AI provider rejected the request (HTTP {status})."),
        429 => new AiException(
            AiErrorKind.RateLimited,
            "The AI provider is rate-limiting requests (HTTP 429)."),
        >= 500 => new AiException(
            AiErrorKind.Unavailable,
            $"The AI provider returned HTTP {status}."),
        _ => new AiException(
            AiErrorKind.InvalidResponse,
            $"The AI provider returned an unexpected HTTP {status}."),
    };

    private static AiErrorKind ProbeStatus(int status) => status switch
    {
        401 or 403 => AiErrorKind.Authentication,
        429 => AiErrorKind.RateLimited,
        _ => AiErrorKind.Unavailable,
    };

    private void LogFailure(HttpMethod method, Uri url, int status, string model)
    {
        // Status, endpoint and model only - never the request/response bodies (they can
        // contain email content) and never the Authorization header.
        _logger.LogWarning(
            "AI request failed. method={Method} url={Url} status={Status} model={Model}",
            method, SafeUrl(url), status, model);
    }

    /// <summary>URL for logging: scheme/host/path only, never query strings.</summary>
    private static string SafeUrl(Uri url)
        => $"{url.Scheme}://{url.Authority}{url.AbsolutePath}";

    // ------------------------------------------------------------------
    // Wire payloads (OpenAI-compatible JSON)
    // ------------------------------------------------------------------

    private sealed class ChatPayload
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("messages")]
        public ChatMessagePayload[] Messages { get; init; } = [];

        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; }
    }

    private sealed class ChatMessagePayload
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;
    }
}

