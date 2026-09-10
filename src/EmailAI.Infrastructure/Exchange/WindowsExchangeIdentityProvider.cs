using System.Security.Principal;
using EmailAI.Application.Exchange;
using EmailAI.Domain.Exchange;

namespace EmailAI.Infrastructure.Exchange;

/// <summary>
/// Detects the current Windows account from the real process token
/// (<see cref="WindowsIdentity.GetCurrent"/>), falling back to the .NET environment
/// properties (<c>Environment.UserDomainName\UserName</c>) when a token is not
/// available (for example service accounts without an interactive token).
/// </summary>
public sealed class WindowsExchangeIdentityProvider : IExchangeIdentityProvider
{
    public DetectedWindowsIdentity GetCurrentIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var fullName = identity.Name;
                if (!string.IsNullOrWhiteSpace(fullName))
                {
                    var separator = fullName.IndexOf('\\');
                    var domain = separator > 0 ? fullName[..separator] : null;
                    var userName = separator > 0 ? fullName[(separator + 1)..] : fullName;
                    return new DetectedWindowsIdentity(
                        userName,
                        domain,
                        fullName,
                        DetectionSource: "WindowsIdentity",
                        IsWindowsIntegratedAuthenticationSupported: true);
                }
            }
            catch (Exception)
            {
                // Fall through to the environment-based detection below.
            }
        }

        var environmentDomain = Environment.UserDomainName;
        var environmentUser = Environment.UserName;
        var environmentFull = string.IsNullOrWhiteSpace(environmentDomain)
            ? environmentUser
            : $"{environmentDomain}\\{environmentUser}";

        return new DetectedWindowsIdentity(
            environmentUser,
            environmentDomain,
            environmentFull,
            DetectionSource: "Environment",
            IsWindowsIntegratedAuthenticationSupported: OperatingSystem.IsWindows());
    }
}
