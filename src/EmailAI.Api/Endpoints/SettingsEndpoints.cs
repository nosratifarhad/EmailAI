using EmailAI.Api.Web;
using EmailAI.Application.AI;
using EmailAI.Application.Exchange;
using EmailAI.Application.Settings;
using EmailAI.Domain.Exchange;
using Microsoft.Extensions.Options;

namespace EmailAI.Api.Endpoints;

/// <summary>
/// Settings surface for the Blazor Settings page:
///   GET    /api/settings/ai            - effective provider settings + key presence
///                                        (NEVER the key)
///   PUT    /api/settings/ai            - store per-user Base URL/Model overrides and/or
///                                        replace the API key in the secure store
///   DELETE /api/settings/ai            - remove the saved API key (secure store)
///   POST   /api/settings/ai/test       - probe the configured provider connection
///   GET    /api/settings/exchange      - effective Exchange settings + detected identity
///                                        (NEVER the password, never a server-managed account)
///   PUT    /api/settings/exchange      - store the per-user Exchange endpoint/mode/account and
///                                        (when supplied) the password in the secure store
///   DELETE /api/settings/exchange      - remove the per-user Exchange configuration + password
///   POST   /api/settings/exchange/test - probe a draft without saving anything
///
/// Secret rules enforced here:
///   * the AI API key and the Exchange password are accepted on PUT and never echoed back by
///     any GET or test response,
///   * store/delete operations go through the per-user secure store + per-user settings store
///     (never appsettings/files that ship with the app),
///   * test/health outcomes are sanitized strings that can never contain a credential,
///   * responses in environment-managed deployments explain the AI_CREDENTIAL_SOURCE
///     restriction without revealing anything sensitive.
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings").WithTags("settings");

        group.MapGet("/ai", GetAiSettingsAsync).WithName("GetAiSettings");
        group.MapPut("/ai", PutAiSettingsAsync).WithName("PutAiSettings");
        group.MapDelete("/ai", DeleteAiSettingsAsync).WithName("DeleteAiSettings");
        group.MapPost("/ai/test", TestAiConnectionAsync).WithName("TestAiConnection");

        group.MapGet("/exchange", GetExchangeSettingsAsync).WithName("GetExchangeSettings");
        group.MapPut("/exchange", PutExchangeSettingsAsync).WithName("PutExchangeSettings");
        group.MapDelete("/exchange", DeleteExchangeSettingsAsync).WithName("DeleteExchangeSettings");
        group.MapPost("/exchange/test", TestExchangeConnectionAsync).WithName("TestExchangeConnection");
    }

    private static async Task<IResult> GetAiSettingsAsync(
        IAiCredentialService credentials,
        CancellationToken cancellationToken)
    {
        var status = await credentials.GetStatusAsync(cancellationToken);
        return Results.Ok(new AiSettingsResponse
        {
            Configured = status.Configured,
            HasApiKey = status.HasApiKey,
            UserManaged = status.UserManaged,
            EffectiveSource = status.EffectiveSource,
            BaseUrl = status.BaseUrl,
            Model = status.Model,
            TimeoutSeconds = status.TimeoutSeconds,
        });
    }

    private static async Task<IResult> PutAiSettingsAsync(
        AiSettingsUpdateRequest? request,
        IAiCredentialService credentials,
        CancellationToken cancellationToken)
    {
        var baseUrl = request?.BaseUrl;
        var model = request?.Model;
        var apiKey = request?.ApiKey;

        try
        {
            await credentials.SaveSettingsAsync(baseUrl, model, apiKey, cancellationToken);
            return Results.NoContent();
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> DeleteAiSettingsAsync(
        IAiCredentialService credentials,
        CancellationToken cancellationToken)
    {
        try
        {
            await credentials.DeleteKeyAsync(cancellationToken);
            return Results.NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> TestAiConnectionAsync(
        IAiClient aiClient,
        IAiCredentialService credentials,
        CancellationToken cancellationToken)
    {
        var status = await credentials.GetStatusAsync(cancellationToken);
        if (!status.Configured)
        {
            return Results.Ok(new AiTestConnectionResponse
            {
                Status = "not_configured",
                Error = "AI is not configured. Configure the provider in Settings or ask the " +
                        "administrator to set AI_BASE_URL and AI_MODEL.",
            });
        }

        if (status.UserManaged && !status.HasApiKey)
        {
            return Results.Ok(new AiTestConnectionResponse
            {
                Status = "not_configured",
                Error = "Save your AI API key first, then test the connection.",
            });
        }

        var health = await aiClient.ProbeAsync(cancellationToken);
        if (health.IsAvailable)
        {
            return Results.Ok(new AiTestConnectionResponse
            {
                Status = "connected",
                LatencyMs = health.LatencyMs,
            });
        }

        return health.ErrorKind switch
        {
            AiErrorKind.Authentication => Results.Ok(new AiTestConnectionResponse
            {
                Status = "authentication_failed",
                Error = "The AI provider rejected the connection. Check the saved API key.",
            }),
            AiErrorKind.RateLimited => Results.Ok(new AiTestConnectionResponse
            {
                Status = "rate_limited",
                Error = "The AI provider is rate-limiting requests. Try again later.",
            }),
            AiErrorKind.Timeout => Results.Ok(new AiTestConnectionResponse
            {
                Status = "timed_out",
                Error = "The AI provider did not respond in time.",
            }),
            _ => Results.Ok(new AiTestConnectionResponse
            {
                Status = "unavailable",
                Error = health.Error,
            }),
        };
    }

    private static async Task<IResult> GetExchangeSettingsAsync(
        IExchangeSettingsService settings,
        IOptionsMonitor<ExchangeOptions> options,
        IExchangeIdentityProvider identityProvider,
        IMailboxIdentityProvider mailboxIdentityProvider,
        CancellationToken cancellationToken)
    {
        var status = await settings.GetStatusAsync(cancellationToken);
        var environment = options.CurrentValue;
        var usesUsernamePassword = !status.UsesDefaultCredentials
            && string.Equals(
                status.Authentication,
                ExchangeOptions.UsernamePasswordMode,
                StringComparison.OrdinalIgnoreCase);

        // Booleans only. A server-managed account is never surfaced - only its presence -
        // while a per-user account is the user's own value and safe to echo back.
        var usernameConfigured = usesUsernamePassword
            && (status.UserManaged
                ? status.Username is not null
                : !string.IsNullOrWhiteSpace(environment.Username));
        var passwordConfigured = usesUsernamePassword
            && (status.UserManaged
                ? status.HasPassword
                : !string.IsNullOrWhiteSpace(environment.Password));

        var identity = identityProvider.GetCurrentIdentity();

        // The authoritative "current user" the AI is told about. Resolved from the Exchange
        // context and cached; it never throws and never carries a credential, so a Settings
        // load cannot fail because of it. A DEPLOYMENT-managed account (a service account the
        // user never typed) is not echoed back - only its presence - which is the same rule the
        // account fields above follow.
        var currentUser = await mailboxIdentityProvider.GetCurrentUserAsync(cancellationToken);
        var hiddenIdentityValues = status.UserManaged
            ? Array.Empty<string>()
            : new[] { environment.Username, environment.Mailbox }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();

        return Results.Ok(new ExchangeSettingsResponse
        {
            Configured = status.Configured,
            UserManaged = status.UserManaged,
            EwsUrl = status.EwsUrl,
            AuthenticationMode = status.Configured || status.UserManaged
                ? status.Authentication
                : string.Empty,
            UsesDefaultCredentials = status.UsesDefaultCredentials,
            Username = status.Username,
            Domain = status.Domain,
            HasPassword = status.HasPassword,
            UsernameConfigured = usernameConfigured,
            PasswordConfigured = passwordConfigured,
            DetectedIdentity = SettingsContractMapping.ToDto(identity),
            CurrentUser = SettingsContractMapping.ToDto(currentUser, hiddenIdentityValues),
            Note = BuildExchangeNote(status),
        });
    }

    private static async Task<IResult> PutExchangeSettingsAsync(
        ExchangeSettingsUpdateRequest? request,
        IExchangeSettingsService settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await settings.SaveAsync(
                new ExchangeSettingsUpdate(
                    request?.EwsUrl,
                    request?.Authentication,
                    request?.Username,
                    request?.Domain,
                    request?.Password),
                cancellationToken);
            return Results.NoContent();
        }
        catch (ArgumentException exception)
        {
            // Includes unsupported modes, a missing account/password and a bad EWS URL -
            // every message is secret-free and nothing was written.
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> DeleteExchangeSettingsAsync(
        IExchangeSettingsService settings,
        CancellationToken cancellationToken)
    {
        await settings.DeleteAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Probes a candidate Exchange configuration without saving anything. An invalid draft is
    /// rejected as a 400 (nothing is persisted either way); the probe itself always answers
    /// 200 with a secret-free status.
    /// </summary>
    private static async Task<IResult> TestExchangeConnectionAsync(
        ExchangeSettingsUpdateRequest? request,
        IExchangeSettingsService settings,
        IExchangeConnectionTester tester,
        CancellationToken cancellationToken)
    {
        ExchangeOptions draft;
        try
        {
            draft = await settings.ResolveTestOptionsAsync(
                new ExchangeSettingsUpdate(
                    request?.EwsUrl,
                    request?.Authentication,
                    request?.Username,
                    request?.Domain,
                    request?.Password),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }

        var result = await tester.TestAsync(draft, cancellationToken);
        return Results.Ok(new ExchangeTestConnectionResponse
        {
            Status = result.IsHealthy ? "connected" : "failed",
            LatencyMs = result.LatencyMs,
            Error = result.IsHealthy ? null : result.Error ?? "Exchange connection failed.",
        });
    }

    private static string BuildExchangeNote(ExchangeSettingsStatus status)
    {
        if (status.UserManaged)
        {
            var account = status.UsesDefaultCredentials
                ? "the current Windows account"
                : $"the saved account '{status.Username}'";

            return "These Exchange settings are saved for this Windows account and are used " +
                   $"as they are. EmailAI authenticates with {account} only: there is no " +
                   "fallback to another credential mechanism, and the password is stored in " +
                   "Windows Credential Manager, never in a file.";
        }

        if (status.UsesDefaultCredentials)
        {
            return "EmailAI is using the deployment's Exchange configuration with Windows " +
                   "authentication (the current Windows account only). There is no automatic " +
                   "fallback to a username/password or NTLM credential mechanism. Save your own " +
                   "Exchange settings below to use this account's configuration instead.";
        }

        if (string.Equals(
                status.Authentication,
                ExchangeOptions.UsernamePasswordMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return "EmailAI is using the deployment's Exchange configuration with an account " +
                   "managed by the server/environment (EXCHANGE_USERNAME / EXCHANGE_PASSWORD). " +
                   "Those values are never shown here and the Windows identity is not used. Save " +
                   "your own Exchange settings below to manage the connection from this app.";
        }

        return "Exchange is not configured with a supported authentication mode. Enter your " +
               "Exchange EWS endpoint and choose Windows or Username + Password below.";
    }
}

