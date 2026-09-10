using System.Diagnostics;
using Microsoft.Exchange.WebServices.Data;

// Live authentication probe - EmailAI diagnostics.
//
// Purpose: reproduce the two independent Exchange authentication modes the
// production service supports so a failing connection can be classified without
// guessing:
//   * default-credentials mode (Windows mode - Windows Integrated Authentication,
//     current process identity);
//   * explicit account mode (UsernamePassword mode - WebCredentials from
//     EWS_DOMAIN / EWS_USERNAME / EWS_PASSWORD).
//
// This probe NEVER prints a password or an Authorization header: explicit credentials
// are read from environment variables only and the mode line shows the account name
// without the secret.
//
// NOTE: this probe tests each path in isolation, exactly like the EmailAI backend
// which uses ONE explicitly configured Exchange authentication mode per operation
// (Exchange:Authentication=Windows or UsernamePassword - see ExchangeAuthRunner).
// There is no automatic fallback between the two modes in the production service;
// the probe's EWS_* variables are its own diagnostic interface (the backend reads
// EXCHANGE_*).
//
// Run:  dotnet run --project tools/EwsAuthProbe
// Env:  EWS_URL (required)   - e.g. https://mail.example.com/EWS/Exchange.asmx
//       EWS_AUTH             - optional: "windows" (default) or "ntlm"
//       EWS_DOMAIN           - NTLM domain (optional, used with EWS_AUTH=ntlm)
//       EWS_USERNAME         - NTLM account (required with EWS_AUTH=ntlm)
//       EWS_PASSWORD         - NTLM password (required with EWS_AUTH=ntlm; never printed)
//
// Exit code 0 when the probe authenticated; 1 otherwise (the printed error is a safe
// classification, never a credential).

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

        if (auth.Equals("ntlm", StringComparison.OrdinalIgnoreCase))
        {
            var userName = Environment.GetEnvironmentVariable("EWS_USERNAME");
            var domain = Environment.GetEnvironmentVariable("EWS_DOMAIN");
            var password = Environment.GetEnvironmentVariable("EWS_PASSWORD");

            if (string.IsNullOrWhiteSpace(userName) || password is null)
            {
                Console.WriteLine("EWS_AUTH=ntlm requires EWS_USERNAME and EWS_PASSWORD (EWS_DOMAIN optional).");
                return 2;
            }

            Console.WriteLine($"Auth mode        : explicit NTLM as {(string.IsNullOrWhiteSpace(domain) ? string.Empty : domain + "\\")}{userName} (password from the environment, never printed)");
            service.Credentials = string.IsNullOrWhiteSpace(domain)
                ? new WebCredentials(userName, password)
                : new WebCredentials(userName, password, domain);
        }
        else
        {
            Console.WriteLine("Auth mode        : UseDefaultCredentials = true (Windows Integrated)");
            service.UseDefaultCredentials = true;
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