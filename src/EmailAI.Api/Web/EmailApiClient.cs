using System.Net.Http.Json;
using System.Text.Json;
using EmailAI.Domain.Mail;

namespace EmailAI.Api.Web;

/// <summary>
/// Thin, typed client for the EmailAI REST surface. The Blazor UI talks to the API
/// exclusively through this client - it never calls EWS or application services
/// directly. Requests are same-origin ("/api/...", "/health") so no CORS is needed
/// and the same client keeps working when the app is later hosted by Docker/Electron.
/// </summary>
public sealed class EmailApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // JSON arrives camelCased (ASP.NET web defaults); Domain models use PascalCase.
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>GET /health - application liveness.</summary>
    public async Task<ServiceHealth> GetServiceHealthAsync(CancellationToken cancellationToken)
        => await GetAsync<ServiceHealth>("health", cancellationToken);

    /// <summary>GET /health/exchange - EWS connectivity probe (healthy or 503 envelope).</summary>
    public async Task<ExchangeHealth> GetExchangeHealthAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("health/exchange", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Both the 200 (healthy) and the 503 (unhealthy) responses carry the same
        // JSON shape, so parse the body in either case.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ExchangeHealth>(body, JsonOptions);
                if (parsed is not null && !string.IsNullOrEmpty(parsed.Status))
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Fall through to the exception path below.
            }
        }

        if (response.IsSuccessStatusCode)
        {
            return new ExchangeHealth { Status = "healthy" };
        }

        throw await EmailApiException.FromResponseAsync(response, cancellationToken);
    }

    /// <summary>GET /api/folders/{folderKey}/messages?offset=&amp;pageSize=</summary>
    public Task<MessagePage> GetFolderMessagesAsync(string folderKey, int offset, int pageSize, CancellationToken cancellationToken)
        => GetAsync<MessagePage>(
            $"api/folders/{Uri.EscapeDataString(folderKey)}/messages?offset={offset}&pageSize={pageSize}",
            cancellationToken);

    /// <summary>
    /// GET /api/folders/{folderKey}/children - the folders nested directly below a folder. The UI
    /// calls this with "inbox" the first time the user expands Inbox, so the first paint never pays
    /// for a folder walk.
    /// </summary>
    public Task<IReadOnlyList<MailFolder>> GetChildFoldersAsync(string parentKey, CancellationToken cancellationToken)
        => GetAsync<IReadOnlyList<MailFolder>>(
            $"api/folders/{Uri.EscapeDataString(parentKey)}/children",
            cancellationToken);

    /// <summary>GET /api/messages/{itemId} - full message.</summary>
    public Task<EmailMessage> GetMessageAsync(string itemId, CancellationToken cancellationToken)
        => GetAsync<EmailMessage>($"api/messages/{Uri.EscapeDataString(itemId)}", cancellationToken);

    /// <summary>GET /api/messages/{itemId}/thread - conversation timeline.</summary>
    public Task<MessageThread> GetThreadAsync(string itemId, CancellationToken cancellationToken)
        => GetAsync<MessageThread>($"api/messages/{Uri.EscapeDataString(itemId)}/thread", cancellationToken);

    /// <summary>
    /// GET /health/ai - AI provider connectivity. Always 200 with status
    /// connected/not_configured/authentication_failed/unavailable.
    /// </summary>
    public Task<AiHealth> GetAiHealthAsync(CancellationToken cancellationToken)
        => GetAsync<AiHealth>("health/ai", cancellationToken);

    /// <summary>
    /// GET /api/ai/readiness - whether the AI assistant can be used right now and why not.
    /// <paramref name="probe"/> additionally contacts the configured provider (slower);
    /// without it only the local configuration/key/model state is checked.
    /// </summary>
    public Task<AiReadinessResponse> GetAiReadinessAsync(bool probe, CancellationToken cancellationToken)
        => GetAsync<AiReadinessResponse>(
            $"api/ai/readiness?probe={(probe ? "true" : "false")}", cancellationToken);

    /// <summary>POST /api/ai/messages/{itemId}/summarize - concise single-message summary.</summary>
    public Task<AiContentResult> SummarizeMessageAsync(string itemId, AiOperationRequest options, CancellationToken cancellationToken)
        => PostAsync<AiOperationRequest, AiContentResult>(
            $"api/ai/messages/{Uri.EscapeDataString(itemId)}/summarize", options, cancellationToken);

    /// <summary>POST /api/ai/messages/{itemId}/thread-summarize - whole-conversation summary.</summary>
    public Task<AiContentResult> SummarizeThreadAsync(string itemId, AiOperationRequest options, CancellationToken cancellationToken)
        => PostAsync<AiOperationRequest, AiContentResult>(
            $"api/ai/messages/{Uri.EscapeDataString(itemId)}/thread-summarize", options, cancellationToken);

    /// <summary>POST /api/ai/messages/{itemId}/suggest-reply - short answer suggestion (never sent automatically).</summary>
    public Task<AiContentResult> SuggestReplyAsync(string itemId, AiOperationRequest options, CancellationToken cancellationToken)
        => PostAsync<AiOperationRequest, AiContentResult>(
            $"api/ai/messages/{Uri.EscapeDataString(itemId)}/suggest-reply", options, cancellationToken);

    /// <summary>POST /api/ai/messages/{itemId}/generate-reply - complete editable draft (never sent automatically).</summary>
    public Task<AiContentResult> GenerateReplyAsync(string itemId, AiOperationRequest options, CancellationToken cancellationToken)
        => PostAsync<AiOperationRequest, AiContentResult>(
            $"api/ai/messages/{Uri.EscapeDataString(itemId)}/generate-reply", options, cancellationToken);

    /// <summary>
    /// GET /api/settings/ai - effective AI provider settings + key presence (the API
    /// key itself is never returned by the server).
    /// </summary>
    public Task<AiSettingsResponse> GetAiSettingsAsync(CancellationToken cancellationToken)
        => GetAsync<AiSettingsResponse>("api/settings/ai", cancellationToken);

    /// <summary>
    /// PUT /api/settings/ai - saves the per-user Base URL/Model overrides and/or
    /// replaces the API key in the per-user secure store. Pass null for a field to
    /// leave it unchanged; pass an empty string for Base URL/Model to clear the
    /// override. The API key travels only from the client to the server and is never
    /// echoed back.
    /// </summary>
    public Task SaveAiSettingsAsync(
        string? baseUrl,
        string? model,
        string? apiKey,
        CancellationToken cancellationToken)
        => SendNoContentAsync(
            HttpMethod.Put,
            "api/settings/ai",
            new AiSettingsUpdateRequest { BaseUrl = baseUrl, Model = model, ApiKey = apiKey },
            cancellationToken);

    /// <summary>DELETE /api/settings/ai - remove the saved API key from the per-user secure store.</summary>
    public Task DeleteAiApiKeyAsync(CancellationToken cancellationToken)
        => SendNoContentAsync(HttpMethod.Delete, "api/settings/ai", body: null, cancellationToken);

    /// <summary>
    /// POST /api/settings/ai/test - probe the configured provider
    /// (connected/not_configured/authentication_failed/rate_limited/timed_out/unavailable).
    /// </summary>
    public Task<AiTestConnectionResponse> TestAiConnectionAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/settings/ai/test");
        return SendAsync<AiTestConnectionResponse>(request, cancellationToken);
    }

    /// <summary>
    /// GET /api/settings/exchange - effective Exchange settings (endpoint, mode, per-user
    /// account), the detected Windows identity and password presence. The password and any
    /// server-managed account value are never returned.
    /// </summary>
    public Task<ExchangeSettingsResponse> GetExchangeSettingsAsync(CancellationToken cancellationToken)
        => GetAsync<ExchangeSettingsResponse>("api/settings/exchange", cancellationToken);

    /// <summary>
    /// PUT /api/settings/exchange - saves the per-user Exchange endpoint/mode/account and,
    /// when supplied, the password (per-user credential store only). Pass null/blank for the
    /// password to keep an already stored one; Windows mode never uses or stores a password.
    /// </summary>
    public Task SaveExchangeSettingsAsync(
        string ewsUrl,
        string authentication,
        string? username,
        string? domain,
        string? password,
        CancellationToken cancellationToken)
        => SendNoContentAsync(
            HttpMethod.Put,
            "api/settings/exchange",
            new ExchangeSettingsUpdateRequest
            {
                EwsUrl = ewsUrl,
                Authentication = authentication,
                Username = username,
                Domain = domain,
                Password = password,
            },
            cancellationToken);

    /// <summary>
    /// DELETE /api/settings/exchange - removes the per-user Exchange configuration and the
    /// stored Exchange password, so the deployment configuration is in effect again.
    /// </summary>
    public Task DeleteExchangeSettingsAsync(CancellationToken cancellationToken)
        => SendNoContentAsync(HttpMethod.Delete, "api/settings/exchange", body: null, cancellationToken);

    /// <summary>
    /// POST /api/settings/exchange/test - probes a draft without saving it
    /// ("connected" / "failed"). Throws <see cref="EmailApiException"/> with a 400 message
    /// when the draft is not a usable configuration.
    /// </summary>
    public Task<ExchangeTestConnectionResponse> TestExchangeConnectionAsync(
        ExchangeSettingsUpdateRequest draft,
        CancellationToken cancellationToken)
        => PostAsync<ExchangeSettingsUpdateRequest, ExchangeTestConnectionResponse>(
            "api/settings/exchange/test", draft, cancellationToken);

    /// <summary>POST /api/messages/{itemId}/reply - sends the reply through EWS.</summary>
    public Task<ReplyResult> ReplyAsync(string itemId, ReplyDraft draft, CancellationToken cancellationToken)
        => PostAsync<ReplyDraft, ReplyResult>(
            $"api/messages/{Uri.EscapeDataString(itemId)}/reply", draft, cancellationToken);

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowOnFailureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new EmailApiException((int)response.StatusCode, "empty_response",
                "The server returned an empty response.", null);
    }

    private async Task<TResult> PostAsync<TBody, TResult>(string path, TBody body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowOnFailureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResult>(JsonOptions, cancellationToken)
            ?? throw new EmailApiException((int)response.StatusCode, "empty_response",
                "The server returned an empty response.", null);
    }

    /// <summary>PUT/DELETE that accepts 204 No Content (nothing to deserialize).</summary>
    private async Task SendNoContentAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowOnFailureAsync(response, cancellationToken);
    }

    private async Task<TResult> SendAsync<TResult>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowOnFailureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResult>(JsonOptions, cancellationToken)
            ?? throw new EmailApiException((int)response.StatusCode, "empty_response",
                "The server returned an empty response.", null);
    }

    private static async Task ThrowOnFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await EmailApiException.FromResponseAsync(response, cancellationToken);
        }
    }
}

