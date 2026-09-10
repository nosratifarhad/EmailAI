namespace EmailAI.Application.Settings;

/// <summary>
/// Failure while reading, writing or deleting a per-user secret (for example the Windows
/// Credential Manager is unavailable, the target is invalid or access was denied). The
/// message NEVER contains the secret value.
/// </summary>
public class SecretStoreException : Exception
{
    public SecretStoreException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The well-known credential-store targets used by EmailAI. Each secret has its own target
/// so one secret can never be read, overwritten or deleted by accident when the other is
/// managed - the Exchange password and the AI API key are fully isolated.
/// </summary>
public static class SecretTargets
{
    /// <summary>AI provider API key (Windows Credential Manager, current user).</summary>
    public const string AiApiKey = "EmailAI/AI";

    /// <summary>Exchange password for explicit username/password authentication.</summary>
    public const string ExchangePassword = "EmailAI/Exchange";
}

/// <summary>
/// A per-user, OS-protected place for a named secret. The desktop implementation is the
/// Windows Credential Manager (scoped to the current Windows account); tests and
/// non-Windows hosts substitute in-memory fakes.
///
/// Implementations must never log, echo or return a stored value through any channel other
/// than <see cref="ReadAsync"/>, and must keep each target independent.
/// </summary>
public interface ISecretStore
{
    /// <summary>True when a value is currently stored for <paramref name="target"/>.</summary>
    Task<bool> ExistsAsync(string target, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored value, or null when nothing is stored for the target.</summary>
    Task<string?> ReadAsync(string target, CancellationToken cancellationToken = default);

    /// <summary>Saves (or replaces) the value for the target.</summary>
    Task WriteAsync(string target, string value, CancellationToken cancellationToken = default);

    /// <summary>Removes the target's value. Removing a missing value is a no-op.</summary>
    Task DeleteAsync(string target, CancellationToken cancellationToken = default);
}
