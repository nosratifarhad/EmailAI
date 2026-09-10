using System.Diagnostics;
using Microsoft.Exchange.WebServices.Data;

// Live diagnostics probe - EmailAI Exchange connectivity.
//
// Purpose: reproduce the two Exchange authentication modes the production service supports, so
// a failing connection can be classified without guessing:
//   * Windows mode          - UseDefaultCredentials (Windows Integrated Authentication, the
//                             current process identity). No username/password is used and
//                             nothing is ever retried with another credential mechanism.
//   * UsernamePassword mode - WebCredentials built from EWS_DOMAIN / EWS_USERNAME /
//                             EWS_PASSWORD (one explicit account, no Windows identity).
//
// This probe NEVER prints a password or an Authorization header: explicit credentials come from
// environment variables only, and the mode line shows the account name without the secret.
//
// Optional second capability: EWS_RESOLVE resolves one or more names through the Exchange
// DIRECTORY (ResolveNames, DirectoryOnly) and prints the display name and SMTP address the
// server returns. It exists because EmailAI's current-user identity is only authoritative when
// that lookup succeeds, and the answer depends on the form of the name that is sent
// ("DOMAIN\user" versus the bare account name). Only non-secret directory data is printed.
//
// NOTE: like the backend, this probe uses exactly ONE explicitly configured mode per run
// (there is no automatic fallback between them - see ExchangeAuthRunner). EWS_* variables are
// this tool's own diagnostic interface; the backend reads EXCHANGE_*.
//
// Run:  dotnet run --project tools/EwsAuthProbe
// Env:  EWS_URL (required)   - e.g. https://mail.example.com/EWS/Exchange.asmx
//       EWS_AUTH             - optional: "windows" (default) or "usernamepassword"
//       EWS_DOMAIN           - domain (optional, used with EWS_AUTH=usernamepassword)
//       EWS_USERNAME         - account (required with EWS_AUTH=usernamepassword)
//       EWS_PASSWORD         - password (required with EWS_AUTH=usernamepassword; never printed)
//       EWS_RESOLVE          - optional: "name1;name2" to resolve through the directory instead
//                              of probing the root folder
//
// Exit code 0 when the probe succeeded; 1 when it failed; 2 on a usage error. Printed errors are
// a safe classification, never a credential.

static class Program
{
    static async Task<int> Main()
    {
        var windowsIdentity = OperatingSystem.IsWindows()
            ? System.Security.Principal.WindowsIdentity.GetCurrent()?.Name
            : null;
        Console.WriteLine($"Process identity : {Environment.UserDomainName}\\{Environment.UserName}{(windowsIdentity is null ? string.Empty : $" (WindowsIdentity: {windowsIdentity})")}");
        Console.WriteLine($"Interactive      : {Environment.UserInteractive}");

        var url = Environment.GetEnvironmentVariable("EWS_URL");
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("EWS_URL environment variable is required.");
            return 2;
        }

        var auth = Environment.GetEnvironmentVariable("EWS_AUTH") ?? "windows";
        Console.WriteLine($"EWS endpoint     : {url}");

        var service = new ExchangeService(ExchangeVersion.Exchange2013_SP1)
        {
            Url = new Uri(url),
            Timeout = 15_000,
            UserAgent = "EmailAI-AuthProbe/1.0",
        };

        if (auth.Equals("usernamepassword", StringComparison.OrdinalIgnoreCase)
            || auth.Equals("username-password", StringComparison.OrdinalIgnoreCase)
            || auth.Equals("username_password", StringComparison.OrdinalIgnoreCase))
        {
            var userName = Environment.GetEnvironmentVariable("EWS_USERNAME");
            var domain = Environment.GetEnvironmentVariable("EWS_DOMAIN");
            var password = Environment.GetEnvironmentVariable("EWS_PASSWORD");

            if (string.IsNullOrWhiteSpace(userName) || password is null)
            {
                Console.WriteLine("EWS_AUTH=usernamepassword requires EWS_USERNAME and EWS_PASSWORD (EWS_DOMAIN optional).");
                return 2;
            }

            Console.WriteLine($"Auth mode        : explicit account {(string.IsNullOrWhiteSpace(domain) ? string.Empty : domain + "\\")}{userName} (password from the environment, never printed)");
            service.Credentials = string.IsNullOrWhiteSpace(domain)
                ? new WebCredentials(userName, password)
                : new WebCredentials(userName, password, domain);
        }
        else if (auth.Equals("windows", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Auth mode        : UseDefaultCredentials = true (Windows Integrated, no fallback)");
            service.UseDefaultCredentials = true;
        }
        else
        {
            Console.WriteLine($"EWS_AUTH must be 'windows' or 'usernamepassword' (received '{auth}').");
            return 2;
        }

        var resolveNames = Environment.GetEnvironmentVariable("EWS_RESOLVE");
        if (!string.IsNullOrWhiteSpace(resolveNames))
        {
            return await ResolveAsync(service, resolveNames);
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var results = await service.FindFolders(WellKnownFolderName.Root, new FolderView(1));
            stopwatch.Stop();
            Console.WriteLine($"RESULT: OK - authenticated in {stopwatch.ElapsedMilliseconds} ms ({results.Folders.Count} folder(s) at root).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"RESULT: FAILED - {Classify(exception)}");
            DumpChain(exception, 0);
            return 1;
        }
    }

    /// <summary>
    /// Resolves each candidate through the Exchange directory and prints the display name and
    /// SMTP address the server returned (non-secret data only). Confirms whether an identity
    /// lookup can make the current user authoritative for a given name form.
    /// </summary>
    private static async Task<int> ResolveAsync(ExchangeService service, string candidates)
    {
        var failed = 0;
        foreach (var raw in candidates.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = raw.Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            try
            {
                var resolutions = await service.ResolveName(
                    candidate,
                    ResolveNameSearchLocation.DirectoryOnly,
                    returnContactDetails: true,
                    new PropertySet(BasePropertySet.FirstClassProperties));

                if (resolutions.Count == 0)
                {
                    Console.WriteLine($"RESOLVE '{candidate}': NO RESULTS");
                    failed++;
                    continue;
                }

                for (var index = 0; index < resolutions.Count; index++)
                {
                    var resolution = resolutions[index];
                    var name = resolution.Mailbox?.Name ?? resolution.Contact?.DisplayName ?? "(no name)";
                    var address = resolution.Mailbox?.Address ?? "(no address)";
                    Console.WriteLine($"RESOLVE '{candidate}' [{index}]: {name} <{address}>");
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"RESOLVE '{candidate}': FAILED - {Classify(exception)}");
                failed++;
            }
        }

        Console.WriteLine(failed == 0
            ? "RESULT: OK - every candidate resolved through the Exchange directory."
            : $"RESULT: PARTIAL - {failed} candidate(s) did not resolve.");
        return failed == 0 ? 0 : 1;
    }

    private static string Classify(Exception exception)
    {
        var text = exception.ToString();
        if (text.Contains("401", StringComparison.Ordinal) || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "Authentication rejected by the server (HTTP 401 Unauthorized).";
        }

        if (text.Contains("403", StringComparison.Ordinal) || text.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return "Access denied by the server (HTTP 403 Forbidden).";
        }

        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase) || text.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "The request timed out.";
        }

        if (text.Contains("Could not establish trust relationship", StringComparison.OrdinalIgnoreCase))
        {
            return "TLS trust failure (server certificate not trusted).";
        }

        return exception.GetType().Name + ": " + FirstLine(exception.Message);
    }

    private static void DumpChain(Exception exception, int depth)
    {
        if (exception is null || depth > 6)
        {
            return;
        }

        var inner = exception.InnerException;
        if (inner is not null)
        {
            Console.WriteLine($"  inner[{depth}] {inner.GetType().Name}: {FirstLine(inner.Message)}");
            DumpChain(inner, depth + 1);
        }
    }

    private static string FirstLine(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line ?? string.Empty;
    }
}