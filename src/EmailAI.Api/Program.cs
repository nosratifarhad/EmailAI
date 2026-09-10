using EmailAI.Api.Components;
using EmailAI.Api.Configuration;
using EmailAI.Api.Endpoints;
using EmailAI.Api.Middleware;
using EmailAI.Api.Web;
using EmailAI.Application.AI;
using EmailAI.Application.Exchange;
using EmailAI.Application.Notifications;
using EmailAI.Application.Settings;
using EmailAI.Domain.AI;
using EmailAI.Domain.Exchange;
using EmailAI.Infrastructure.AI;
using EmailAI.Infrastructure.Exchange;
using EmailAI.Infrastructure.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Structured logging (console + optional Application Insights/Serilog sinks later).
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IExchangeMailService, EwsExchangeMailService>();
builder.Services.ConfigureExchangeOptions(builder.Configuration);

// AI (any OpenAI-compatible provider). Deliberately optional at startup: when no
// base URL/model is configured the app runs normally and reports "AI is not
// configured" instead of crashing. Configured values are read again per request, so
// changing the environment takes effect without a restart.
builder.Services.ConfigureAiOptions(builder.Configuration);

// Per-user configuration infrastructure. Everything non-secret (Exchange endpoint/mode/
// account and the AI base URL/model) lives in ONE per-user document,
// %APPDATA%\EmailAI\settings.json, and every secret lives in the Windows Credential
// Manager under its own target (EmailAI/AI, EmailAI/Exchange) - never in a file, an
// appsettings value, a log or an API response.
//  * WindowsSecretStore        - the per-user, OS-protected secret store (CredRead/CredWrite).
//  * FileUserSettingsStore     - the per-user, non-secret settings document (atomic writes,
//                                corrupt-file quarantine, one-time ai-settings.json migration).
//  * WindowsCredentialStore    - IAiCredentialStore over the EmailAI/AI target (the
//                                long-standing target, kept for backward compatibility).
//  * UserSettingsAiStore       - IAiUserSettingsStore over the same unified document.
//  * AiCredentialCoordinator   - implements IAiCredentialProvider (runtime key for
//                                OpenAiCompatClient), IAiCredentialService (Settings/health
//                                API surface) and IAiConfigurationProvider (effective
//                                provider settings = server configuration merged with the
//                                per-user overrides) behind one precedence policy. It is
//                                deliberately a singleton so every request and circuit reads
//                                the same state.
//  * ExchangeConfigurationProvider - the EFFECTIVE Exchange settings: the per-user
//                                configuration as a whole, else the deployment's environment
//                                configuration (never a per-field merge).
//  * ExchangeSettingsService   - the only writer of the per-user Exchange configuration.
//  * EwsExchangeConnectionTester - POST /api/settings/exchange/test (EWS root-folder probe
//                                through the single configured auth mode).
//  * WindowsExchangeIdentityProvider - surfaces the current Windows account for the
//                                read-only Settings display (Exchange may use Windows
//                                Integrated Auth).
builder.Services.AddSingleton<ISecretStore, WindowsSecretStore>();
builder.Services.AddSingleton<IUserSettingsStore, FileUserSettingsStore>();
builder.Services.AddSingleton<IAiCredentialStore, WindowsCredentialStore>();
builder.Services.AddSingleton<IAiUserSettingsStore, UserSettingsAiStore>();
builder.Services.AddSingleton<AiCredentialCoordinator>();
builder.Services.AddSingleton<IAiCredentialProvider>(sp => sp.GetRequiredService<AiCredentialCoordinator>());
builder.Services.AddSingleton<IAiCredentialService>(sp => sp.GetRequiredService<AiCredentialCoordinator>());
builder.Services.AddSingleton<IAiConfigurationProvider>(sp => sp.GetRequiredService<AiCredentialCoordinator>());
builder.Services.AddSingleton<IExchangeConfigurationProvider, ExchangeConfigurationProvider>();
builder.Services.AddSingleton<IExchangeSettingsService, ExchangeSettingsService>();
builder.Services.AddSingleton<IExchangeConnectionTester, EwsExchangeConnectionTester>();
builder.Services.AddSingleton<IExchangeIdentityProvider, WindowsExchangeIdentityProvider>();

// The AUTHORITATIVE current user (the mailbox owner) resolved from the Exchange context and
// handed to the AI. Registered as a singleton so its bounded cache is shared by every request
// and circuit; a settings change is still picked up immediately because the cache key covers
// endpoint/mode/account/mailbox.
builder.Services.AddSingleton<IMailboxIdentityProvider, EwsMailboxIdentityProvider>();

// New-mail change detection for the Windows/Electron shell's native notifications. Singleton
// on purpose: the baseline/deduplication state must survive individual requests (and must be
// empty again after a restart, which is what keeps a fresh launch quiet).
builder.Services.AddSingleton<IMailNotificationService, MailNotificationService>();

// Propagates "settings changed" to every live circuit so the header status updates
// immediately instead of waiting for the next health poll.
builder.Services.AddSingleton<AppStatusNotifier>();

builder.Services.AddHttpClient<OpenAiCompatClient>(static client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddScoped<IAiClient>(sp => sp.GetRequiredService<OpenAiCompatClient>());
builder.Services.AddScoped<IAiService, AiService>();

// Answers "can the AI assistant be used right now, and why not?" BEFORE an AI operation is
// attempted, so the UI can explain the missing piece instead of showing a raw failure.
builder.Services.AddScoped<IAiReadinessService, AiReadinessService>();


// Blazor Web App UI (Interactive Server). The UI is served by this same process, so
// the typed API client below talks to the same origin ("/api/*", "/health") and no
// CORS is required. This is also the shape Docker/Electron will host later.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// One HttpClient per Blazor circuit, pointing back at this application's own origin.
// The timeout is infinite on purpose: every operation that can hang (EWS, AI, mail)
// is bounded server-side by its own configuration, and AI requests pass a circuit
// cancellation token, so a fixed client timeout would only cut long AI calls short.
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(sp.GetRequiredService<NavigationManager>().BaseUri),
    Timeout = Timeout.InfiniteTimeSpan,
});

// Typed client used by the UI components to call the existing REST API.
builder.Services.AddScoped<EmailApiClient>();

var app = builder.Build();

// Warn early (but do not crash) when Exchange is not configured yet. A per-user
// configuration saved in Settings is authoritative and is read per operation, so this
// early warning can only talk about the deployment configuration.
var exchange = app.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;
if (string.IsNullOrWhiteSpace(exchange.EwsUrl))
{
    app.Logger.LogWarning(
        "Exchange is not configured (EXCHANGE_EWS_URL is empty). Mail endpoints will " +
        "return errors until it is set here or a user configures Exchange in Settings.");
}
else if (ExchangeOptions.IsSamplePlaceholder(exchange.EwsUrl))
{
    // appsettings.json ships a documentation-only value (mail.example.com); it must
    // never be mistaken for runtime configuration. Environment variables always win.
    app.Logger.LogWarning(
        "Exchange:EwsUrl is still the documentation placeholder {EwsUrl}. Set " +
        "EXCHANGE_EWS_URL to the real Exchange EWS endpoint, or configure Exchange in " +
        "Settings - mail endpoints report 'not configured' until then.",
        exchange.EwsUrl);
}

// Warn early (but do not crash) when AI is not configured yet - the email client
// keeps working and the UI reports "AI is not configured".
var aiOptions = app.Services.GetRequiredService<IOptions<AiOptions>>().Value;
if (!aiOptions.IsConfigured)
{
    app.Logger.LogWarning(
        "AI is not configured (no base URL or model). " +
        "/health/ai and the AI assistant features will report 'not configured' until " +
        "the provider is configured in Settings or AI_BASE_URL/AI_MODEL are set.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

// JSON error envelope for API/mail endpoints and Blazor prerenders. Registered after the
// HTML exception handler so it runs *innermost* - API failures keep their structured JSON
// responses (503/404/...), while genuinely unexpected request-level failures above it can
// still reach the /Error page.
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Physical wwwroot assets + fingerprinted assets referenced through the Assets API.
app.MapStaticAssets();

app.MapHealthEndpoints();
app.MapMailEndpoints();
app.MapAiEndpoints();
app.MapSettingsEndpoints();
app.MapNotificationEndpoints();

// Keep JSON 404 semantics for unknown API paths (the Blazor fallback below would
// otherwise render the HTML "not found" page for them).
app.Map("/api/{**path}", () => Results.Json(
    new { error = new { code = "not_found", message = "The requested API endpoint was not found." } },
    statusCode: StatusCodes.Status404NotFound));

// Blazor Web App UI. Mapped last - it acts as the fallback for all non-API routes.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Exposed so integration tests can bootstrap the API via WebApplicationFactory.
public partial class Program;
