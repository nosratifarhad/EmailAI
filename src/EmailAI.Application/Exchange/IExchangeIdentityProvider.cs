using EmailAI.Domain.Exchange;

namespace EmailAI.Application.Exchange;

/// <summary>
/// Detects the Windows account the current process runs under, so Exchange can use
/// Windows Integrated Authentication without the user typing a domain, user name or
/// password. No credential material is ever returned - only the identity to display.
/// </summary>
public interface IExchangeIdentityProvider
{
    /// <summary>Returns the detected current Windows identity.</summary>
    DetectedWindowsIdentity GetCurrentIdentity();
}
