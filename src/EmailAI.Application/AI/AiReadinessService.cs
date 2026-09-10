namespace EmailAI.Application.AI;

/// <summary>
/// Default <see cref="IAiReadinessService"/>: checks the effective provider configuration
/// (via <see cref="IAiCredentialService"/>, which never exposes the key) and, when asked,
/// probes the provider through <see cref="IAiClient"/>.
///
/// It never throws for an environment problem and never returns key material: every failure
/// becomes one of the documented <see cref="AiReadinessStatus"/> values with a user-facing
/// message from <see cref="AiUserMessages"/>.
/// </summary>
public sealed class AiReadinessService(
    IAiCredentialService credentials,
    IAiClient client) : IAiReadinessService
{
    public async Task<AiReadiness> GetReadinessAsync(bool probe, CancellationToken cancellationToken = default)
    {
        AiCredentialStatus status;
        try
        {
            status = await credentials.GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiCredentialStoreException)
        {
            // A credential-store failure must not look like "not configured": it needs the
            // user to fix the store (sign in to Windows), which the message says.
            return AiUserMessages.ForErrorCode("ai_credential_store_failed");
        }
        catch (Exception)
        {
            // The credential service is the only dependency here; anything else is a
            // configuration problem, never something the user should see as a stack trace.
            return AiUserMessages.ForStatus(AiReadinessStatus.InvalidConfiguration, canOpenSettings: true);
        }

        var configured = status.Configured && !string.IsNullOrWhiteSpace(status.BaseUrl);
        if (!configured)
        {
            return AiUserMessages.ForStatus(AiReadinessStatus.NotConfigured, status.UserManaged);
        }

        if (!IsHttpUrl(status.BaseUrl))
        {
            return AiUserMessages.ForStatus(AiReadinessStatus.InvalidConfiguration, status.UserManaged);
        }

        if (string.IsNullOrWhiteSpace(status.Model))
        {
            return AiUserMessages.ForStatus(AiReadinessStatus.MissingModel, status.UserManaged);
        }

        // "UserManaged" means the key must come from this Windows account's secure store.
        // An environment-managed deployment carries its key in configuration instead.
        if (status.UserManaged && !status.HasApiKey)
        {
            return AiUserMessages.ForStatus(AiReadinessStatus.MissingApiKey, canOpenSettings: true);
        }

        if (!probe)
        {
            return AiUserMessages.ForStatus(AiReadinessStatus.Ready, status.UserManaged);
        }

        try
        {
            var result = await client.ProbeAsync(cancellationToken);
            if (result.IsAvailable)
            {
                return AiUserMessages.ForStatus(AiReadinessStatus.Ready, status.UserManaged);
            }

            var state = result.ErrorKind switch
            {
                AiErrorKind.Authentication => AiReadinessStatus.AuthenticationFailed,
                AiErrorKind.RateLimited => AiReadinessStatus.RateLimited,
                AiErrorKind.Timeout => AiReadinessStatus.Timeout,
                AiErrorKind.NotConfigured => AiReadinessStatus.NotConfigured,
                AiErrorKind.InvalidConfiguration => AiReadinessStatus.InvalidConfiguration,
                AiErrorKind.InvalidResponse => AiReadinessStatus.RequestFailed,
                _ => AiReadinessStatus.Unreachable,
            };

            return AiUserMessages.ForStatus(state, status.UserManaged);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiException exception)
        {
            var state = exception.Kind switch
            {
                AiErrorKind.NotConfigured => AiReadinessStatus.NotConfigured,
                AiErrorKind.InvalidConfiguration => AiReadinessStatus.InvalidConfiguration,
                AiErrorKind.Authentication => AiReadinessStatus.AuthenticationFailed,
                AiErrorKind.RateLimited => AiReadinessStatus.RateLimited,
                AiErrorKind.Timeout => AiReadinessStatus.Timeout,
                _ => AiReadinessStatus.Unreachable,
            };

            return AiUserMessages.ForStatus(state, status.UserManaged);
        }
        catch (Exception)
        {
            return AiUserMessages.ForStatus(AiReadinessStatus.Unreachable, status.UserManaged);
        }
    }

    private static bool IsHttpUrl(string? baseUrl)
        => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";
}
