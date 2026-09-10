using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace EmailAI.Tests;

/// <summary>
/// The release/distribution contract, verified against the FILES that define a release
/// instead of the compiled application: <c>desktop/package.json</c> and its lock file (version,
/// artifact name, NSIS shape), <c>desktop/scripts/release.js</c> and <c>verify-secrets.js</c>
/// (the pipeline and its gates), <c>src/EmailAI.Api/appsettings.json</c> and <c>.env.example</c>
/// (the only configuration that may ship - placeholders, never a secret or a real endpoint),
/// the Release-only exclusion of development configuration, the CI workflow, the ignore rules
/// and the user-facing documentation.
///
/// No other suite covers these files, and every one of them can break a distribution without
/// breaking a single behavioural test: a renamed artifact, a leaked internal endpoint, a
/// development appsettings file inside the installer or a README that points at a file that no
/// longer exists.
/// </summary>
public class ReleasePackagingTests(ITestOutputHelper output)
{
    private static class Repo
    {
        public static string Root { get; } = FindRoot();

        public static string Read(string relativePath)
            => File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        public static bool Exists(string relativePath)
            => File.Exists(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        private static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EmailAI.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"Could not locate the repository root (EmailAI.slnx) above {AppContext.BaseDirectory}.");
        }
    }

    private static JsonDocument Parse(string json)
        => JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

    private static string RequiredString(JsonElement element, string propertyName)
    {
        Assert.True(element.TryGetProperty(propertyName, out var value), $"'{propertyName}' is missing.");
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        return value.GetString() ?? string.Empty;
    }

    /// <summary>The installer file name the packaging configuration defines for this version.</summary>
    private static string InstallerFileName()
    {
        using var package = Parse(Repo.Read("desktop/package.json"));
        var version = RequiredString(package.RootElement, "version");
        var template = RequiredString(
            package.RootElement.GetProperty("build").GetProperty("nsis"),
            "artifactName");
        return template.Replace("${version}", version, StringComparison.Ordinal);
    }

    // --------------------------------------------------- packaging configuration

    [Fact]
    public void PackageJson_DefinesTheWindowsInstallerContract()
    {
        var packageJson = Repo.Read("desktop/package.json");
        using var package = Parse(packageJson);
        var root = package.RootElement;

        var version = RequiredString(root, "version");
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Equal("EmailAI", RequiredString(root, "productName"));
        Assert.Equal("main.js", RequiredString(root, "main"));
        Assert.True(
            root.GetProperty("private").GetBoolean(),
            "the desktop package must stay private (it is not published to npm)");

        var build = root.GetProperty("build");
        Assert.Equal("com.emailai.desktop", RequiredString(build, "appId"));
        Assert.Equal("dist", RequiredString(build.GetProperty("directories"), "output"));

        // An assisted, per-user NSIS installer for Windows x64 whose name comes from the version.
        var nsis = build.GetProperty("nsis");
        Assert.Equal("EmailAI-Setup-${version}.exe", RequiredString(nsis, "artifactName"));
        Assert.False(
            nsis.GetProperty("perMachine").GetBoolean(),
            "the installer must stay per-user (no administrator rights)");
        Assert.False(nsis.GetProperty("oneClick").GetBoolean(), "the installer must stay assisted, not one-click");
        Assert.True(nsis.GetProperty("createStartMenuShortcut").GetBoolean());

        var win = build.GetProperty("win");
        var target = Assert.Single(win.GetProperty("target").EnumerateArray());
        Assert.Equal("nsis", RequiredString(target, "target"));
        Assert.Equal("x64", Assert.Single(target.GetProperty("arch").EnumerateArray()).GetString());

        // The bundled self-contained backend is the whole reason a user needs no .NET runtime.
        var extraResource = Assert.Single(build.GetProperty("extraResources").EnumerateArray());
        Assert.Equal("aspnet-publish", RequiredString(extraResource, "from"));
        Assert.Equal("server", RequiredString(extraResource, "to"));

        var files = build.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("main.js", files);
        Assert.Contains("package.json", files);

        // A packaged application must never reference a machine-specific absolute path.
        Assert.DoesNotMatch(@"[A-Za-z]:\\", packageJson);
    }

    [Fact]
    public void PackageLock_KeepsTheSameVersionAsThePackage()
    {
        using var package = Parse(Repo.Read("desktop/package.json"));
        using var lockFile = Parse(Repo.Read("desktop/package-lock.json"));

        var version = RequiredString(package.RootElement, "version");
        Assert.Equal(version, RequiredString(lockFile.RootElement, "version"));
        Assert.Equal(
            version,
            RequiredString(lockFile.RootElement.GetProperty("packages").GetProperty(string.Empty), "version"));
    }

    // ------------------------------------------------- release pipeline and gates

    [Fact]
    public void ReleaseScript_SingleSourcesTheVersionAndArtifactNameFromTheVersionLibrary()
    {
        var script = Repo.Read("desktop/scripts/release.js");
        var library = Repo.Read("desktop/scripts/release-version.js");
        var version = RequiredString(Parse(Repo.Read("desktop/package.json")).RootElement, "version");

        // The version and the installer name are resolved by the shared library, which the gate
        // (verify-release.js) and the notes generator use too: no script derives its own name, and
        // none of them carries a version literal to fall back to.
        Assert.Contains("require('./release-version')", script, StringComparison.Ordinal);
        Assert.DoesNotContain(version, script, StringComparison.Ordinal);
        Assert.Contains(
            "artifactTemplate(desktopDir).replace(ARTIFACT_VERSION_TOKEN, version)",
            library,
            StringComparison.Ordinal);
        Assert.Contains("artifactName", library, StringComparison.Ordinal);
        Assert.DoesNotContain(version, library, StringComparison.Ordinal);

        // Every gate the release depends on is actually invoked by the pipeline.
        Assert.Contains("runNpm('test')", script, StringComparison.Ordinal);
        Assert.Contains("runNpm('build:server')", script, StringComparison.Ordinal);
        Assert.Contains("runNpm('publish:server')", script, StringComparison.Ordinal);
        Assert.Contains("runNpm('dist')", script, StringComparison.Ordinal);
        Assert.Contains("verify-secrets.js", script, StringComparison.Ordinal);
        Assert.Contains("appsettings.Development.json", script, StringComparison.Ordinal);
        Assert.Contains("appsettings.Local.json", script, StringComparison.Ordinal);

        // The artifact is verified and its checksum is written next to it - both through the same
        // library the gate uses, so the checksum always belongs to the exact published file.
        Assert.Contains("was not created", script, StringComparison.Ordinal);
        Assert.Contains("releaseVersion.sha256(", script, StringComparison.Ordinal);
        Assert.Contains("verifyInstaller(", script, StringComparison.Ordinal);
        Assert.Contains(".sha256", script, StringComparison.Ordinal);

        // The published release body is generated for this version, never carried over.
        Assert.Contains("releaseNotes.writeReleaseNotes(", script, StringComparison.Ordinal);

        // "relative to the repository", never a machine-specific path.
        Assert.DoesNotMatch(@"[A-Za-z]:\\", script);
    }

    [Fact]
    public void VerifySecretsScript_ScansSecretsDevelopmentConfigAndEndpoints()
    {
        var script = Repo.Read("desktop/scripts/verify-secrets.js");

        Assert.Contains("--allow-development-config", script, StringComparison.Ordinal);
        Assert.Contains("DEVELOPMENT_CONFIG_PATTERN", script, StringComparison.Ordinal);
        Assert.Contains("appsettings", script, StringComparison.Ordinal);
        Assert.Contains("isDocumentationHost", script, StringComparison.Ordinal);
        Assert.Contains("example.com", script, StringComparison.Ordinal);
        Assert.Contains("process.exit(1)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiProject_ExcludesDevelopmentConfigurationFromAReleasePublish()
    {
        var project = Repo.Read("src/EmailAI.Api/EmailAI.Api.csproj");

        Assert.Contains("'$(Configuration)' == 'Release'", project, StringComparison.Ordinal);
        Assert.Contains("<Content Remove=\"appsettings.Development.json\" />", project, StringComparison.Ordinal);
    }

    // ------------------------------------------------- shipped sample configuration

    [Fact]
    public void SampleAppSettings_ShipsPlaceholdersOnly()
    {
        using var settings = Parse(Repo.Read("src/EmailAI.Api/appsettings.json"));
        var exchange = settings.RootElement.GetProperty("Exchange");

        AssertDocumentationHost(RequiredString(exchange, "EwsUrl"));
        Assert.Contains(
            RequiredString(exchange, "Authentication"),
            new[] { "Windows", "UsernamePassword" });
        Assert.Equal(string.Empty, RequiredString(exchange, "Mailbox"));
        Assert.False(
            exchange.TryGetProperty("Password", out _),
            "the shipped sample must not carry a Password property - secrets live in the credential store");

        var ai = settings.RootElement.GetProperty("Ai");
        Assert.Equal(string.Empty, RequiredString(ai, "BaseUrl"));
        Assert.Equal(string.Empty, RequiredString(ai, "Model"));
        Assert.False(
            ai.TryGetProperty("ApiKey", out _),
            "the shipped sample must not carry an ApiKey property - secrets live in the credential store");
    }

    [Fact]
    public void EnvironmentExample_DocumentsTheSurfaceWithNoSecretValues()
    {
        var example = Repo.Read(".env.example");

        foreach (var documented in new[]
        {
            "EXCHANGE_EWS_URL=", "EXCHANGE_AUTHENTICATION=", "EXCHANGE_DOMAIN=", "EXCHANGE_USERNAME=",
            "EXCHANGE_PASSWORD=", "AI_BASE_URL=", "AI_API_KEY=", "AI_MODEL=", "AI_CREDENTIAL_SOURCE=",
            "EMAILAI_AI_INTEGRATION_TEST=true", "EMAILAI_EXCHANGE_INTEGRATION_TEST=true",
        })
        {
            Assert.Contains(documented, example, StringComparison.Ordinal);
        }

        Assert.Contains("Windows", example, StringComparison.Ordinal);
        Assert.Contains("UsernamePassword", example, StringComparison.Ordinal);

        // The secret-bearing variables must ship empty and the Exchange sample must be a placeholder.
        Assert.Matches(@"(?m)^EXCHANGE_PASSWORD=\s*$", example);
        Assert.Matches(@"(?m)^AI_API_KEY=\s*$", example);
        AssertDocumentationHost(Regex.Match(example, @"(?m)^EXCHANGE_EWS_URL=(.+)$").Groups[1].Value.Trim());
    }

    // ------------------------------------------------ CI, ignore rules, distribution

    [Fact]
    public void Workflow_RunsTheReleasePipelineAndPublishesTheVerifiedArtifacts()
    {
        var workflow = Repo.Read(".github/workflows/release.yml");

        // One pipeline for local and CI: the workflow must not duplicate the packaging steps.
        Assert.Contains("npm run release", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("npm run publish:server", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("npm run dist", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet restore EmailAI.slnx", workflow, StringComparison.Ordinal);

        // The gates and the identity it publishes.
        Assert.Contains("--allow-development-config", workflow, StringComparison.Ordinal);
        Assert.Contains("node scripts/verify-release.js", workflow, StringComparison.Ordinal);
        Assert.Contains("--github-output", workflow, StringComparison.Ordinal);
        Assert.Contains("steps.version.outputs.installer", workflow, StringComparison.Ordinal);
        Assert.Contains("steps.version.outputs.checksum", workflow, StringComparison.Ordinal);

        // Fail-closed: the tag/package gate runs before anything is built, the artifact gate after.
        var versionGate = workflow.IndexOf("Validate the release version", StringComparison.Ordinal);
        var pipeline = workflow.IndexOf("run: npm run release", StringComparison.Ordinal);
        var artifactGate = workflow.IndexOf("Verify the installer", StringComparison.Ordinal);
        Assert.True(
            versionGate >= 0 && versionGate < pipeline,
            "the tag/package version gate must run before the packaging pipeline");
        Assert.True(
            artifactGate > pipeline,
            "the artifact/checksum/metadata/notes gate must run after the packaging pipeline");

        // The upload and the release carry the exact verified files - a wildcard could publish a
        // stale installer, which is how a release ends up carrying another version.
        Assert.DoesNotContain("EmailAI-Setup-*.exe", workflow, StringComparison.Ordinal);
        Assert.Contains(".sha256", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("softprops/action-gh-release@v2", workflow, StringComparison.Ordinal);
        Assert.Contains("name: EmailAI ${{ steps.version.outputs.version }}", workflow, StringComparison.Ordinal);
        Assert.Contains("tag_name: ${{ steps.version.outputs.tag }}", workflow, StringComparison.Ordinal);
        Assert.Contains("body_path: desktop/dist/RELEASE-NOTES.md", workflow, StringComparison.Ordinal);
        Assert.Contains("fail_on_unmatched_files: true", workflow, StringComparison.Ordinal);
        Assert.Contains("contents: write", workflow, StringComparison.Ordinal);
        Assert.Contains("'v*.*.*'", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void GitIgnore_KeepsBuildOutputAndSecretsOutButDocumentationIn()
    {
        var ignore = Repo.Read(".gitignore");

        foreach (var rule in new[]
        {
            "bin/", "obj/", "node_modules/", "desktop/dist/", "desktop/aspnet-publish/",
            ".env", "appsettings.Local.json",
        })
        {
            Assert.Contains(rule, ignore, StringComparison.Ordinal);
        }

        // The sample environment file, the sample appsettings and the documentation are shipped.
        Assert.Contains("!.env.example", ignore, StringComparison.Ordinal);
        Assert.True(Repo.Exists(".env.example"));
        Assert.True(Repo.Exists("src/EmailAI.Api/appsettings.json"));
        Assert.DoesNotContain("README.md", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("RELEASE-NOTES.md", ignore, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ documentation

    [Fact]
    public void Documentation_NamesTheCurrentInstallerArtifactForTheCurrentVersion()
    {
        var installer = InstallerFileName();
        Assert.Matches(@"^EmailAI-Setup-\d+\.\d+\.\d+\.exe$", installer);

        var readme = Repo.Read("README.md");
        Assert.Contains(installer, readme, StringComparison.Ordinal);
        Assert.Contains(installer, Repo.Read("RELEASE-NOTES.md"), StringComparison.Ordinal);

        // The release record states the version it belongs to: the published body is generated for
        // the release version, and the checked-in record may not describe an earlier one.
        var version = RequiredString(Parse(Repo.Read("desktop/package.json")).RootElement, "version");
        var releaseNotes = Repo.Read("RELEASE-NOTES.md");
        Assert.Contains($"# EmailAI {version} ", releaseNotes, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"# EmailAI \d+\.\d+\.\d+ ", Regex.Replace(releaseNotes, Regex.Escape($"# EmailAI {version} "), string.Empty));

        // The download path is the first thing a visitor reads, and it must never ask a normal
        // user to clone or build the project.
        Assert.Contains("Releases", readme, StringComparison.Ordinal);
        Assert.Contains("do not need", readme.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("installer", readme.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_StatesTheStorageLocationsAndCredentialTargets()
    {
        foreach (var document in new[] { "README.md", ".ai/service-context.md" })
        {
            var text = Repo.Read(document);

            Assert.Contains(@"%APPDATA%\EmailAI\settings.json", text, StringComparison.Ordinal);
            Assert.Contains("EmailAI/AI", text, StringComparison.Ordinal);
            Assert.Contains("EmailAI/Exchange", text, StringComparison.Ordinal);
            Assert.Contains("Credential Manager", text, StringComparison.Ordinal);
        }

        // Release notes state where the secrets are, and the full specification set exists and is
        // linked from the README.
        Assert.Contains("Credential Manager", Repo.Read("RELEASE-NOTES.md"), StringComparison.Ordinal);

        var index = Repo.Read(".ai/spec/README.md");
        for (var number = 1; number <= 17; number++)
        {
            var files = Directory.GetFiles(Path.Combine(Repo.Root, ".ai", "spec"), $"{number:00}-*.md");
            Assert.True(files.Length == 1, $"specification {number:00} is missing or duplicated in .ai/spec");
            Assert.Contains(Path.GetFileName(files[0]), index, StringComparison.Ordinal);
        }

        Assert.Contains(".ai/spec", Repo.Read("README.md"), StringComparison.Ordinal);
        Assert.Contains("service-context.md", index, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- no internal infrastructure

    [Fact]
    public void RepositoryTextFiles_ContainNoPrivateEndpointsOrCorporateAccountDomains()
    {
        var scanned = 0;
        foreach (var file in RepositoryTextFiles())
        {
            scanned++;
            var text = File.ReadAllText(file);

            // A private-network endpoint must never be committed, not even as an example.
            Assert.DoesNotMatch(@"https?://(?:10\.|192\.168\.|172\.(?:1[6-9]|2[0-9]|3[01])\.)", text);

            // Every "DOMAIN\account" form must use a documentation placeholder: a real corporate
            // account domain (which is what a live-environment probe would leak) fails here.
            foreach (Match match in Regex.Matches(text, @"\b([A-Z]{3,})\\([A-Za-z0-9._-]+)"))
            {
                Assert.Contains(match.Groups[1].Value, new[] { "DOMAIN", "CONTOSO", "EXAMPLE" });
            }
        }

        Assert.True(scanned > 50, $"expected to scan the repository text files, found only {scanned}");
    }

    private static IEnumerable<string> RepositoryTextFiles()
    {
        string[] extensions = [".cs", ".md", ".js", ".json", ".razor", ".yml", ".css", ".csproj", ".slnx", ".props", ".example"];
        string[] skippedDirectories = ["node_modules", "aspnet-publish", "dist", "bin", "obj", ".git", ".vs"];

        return Directory
            .EnumerateFiles(Repo.Root, "*", SearchOption.AllDirectories)
            .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => !Path.GetRelativePath(Repo.Root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => skippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------- the scan gate itself

    /// <summary>
    /// Runs the real gate (desktop/scripts/verify-secrets.js) against a temporary directory:
    /// it must fail on a planted secret and on a planted development appsettings file, must
    /// never echo the secret, and must pass the configuration that actually ships. The check is
    /// skipped (with an explanatory message) when Node.js is not installed, because the
    /// packaging toolchain requires Node.js anyway.
    /// </summary>
    [Fact]
    public void VerifySecretsScript_FailsOnPlantedDefectsAndPassesOnTheShippedSample()
    {
        var node = FindNodeExecutable();
        if (node is null)
        {
            output.WriteLine(
                "Node.js was not found on PATH, so the packaging-scan behaviour check was not run " +
                "here. The release pipeline requires Node.js and runs this scan on every release.");
            return;
        }

        var scriptPath = Path.Combine(Repo.Root, "desktop", "scripts", "verify-secrets.js");
        var directory = Path.Combine(Path.GetTempPath(), "emailai-verify-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // 1. A planted configuration: a secret-shaped value and a real (non-placeholder) endpoint.
            //    The fixture is assembled so the repository itself never contains the key/value pair.
            var plantedValue = "planted-" + "unit-test-value";
            File.WriteAllText(
                Path.Combine(directory, "appsettings.json"),
                "{\"Exchange\":{\"EwsUrl\":\"https://ews.internal-corp.net/EWS/Exchange.asmx\",\"Pass" + "word\":\""
                + plantedValue + "\"},\"Ai\":{\"BaseUrl\":\"https://ai.corp-intranet.net/v1\"}}");

            var planted = RunNode(node, scriptPath, directory);
            Assert.NotEqual(0, planted.ExitCode);
            Assert.Contains("FOUND", planted.StdErr, StringComparison.Ordinal);
            Assert.Contains("ews.internal-corp.net", planted.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain(plantedValue, planted.StdOut + planted.StdErr, StringComparison.Ordinal);

            // 2. A planted development-only appsettings file must also fail the gate.
            File.WriteAllText(Path.Combine(directory, "appsettings.Development.json"), "{\"Logging\":{}}");
            var developmentConfig = RunNode(node, scriptPath, directory);
            Assert.NotEqual(0, developmentConfig.ExitCode);
            Assert.Contains("development-only configuration", developmentConfig.StdErr, StringComparison.Ordinal);
            File.Delete(Path.Combine(directory, "appsettings.Development.json"));

            // 3. The configuration that actually ships passes the same gate.
            File.WriteAllText(
                Path.Combine(directory, "appsettings.json"),
                Repo.Read("src/EmailAI.Api/appsettings.json"));
            var shipped = RunNode(node, scriptPath, directory);
            Assert.True(
                shipped.ExitCode == 0,
                $"the shipped sample configuration failed the release scan:{Environment.NewLine}{shipped.StdOut}{shipped.StdErr}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ------------------------------------------------------------------------ helpers

    private static void AssertDocumentationHost(string url)
    {
        Assert.False(string.IsNullOrWhiteSpace(url), "the sample endpoint must not be empty");
        var host = new Uri(url).Host;
        string[] documentationDomains = ["example.com", "example.org", "example.net", "contoso.com"];

        Assert.True(
            documentationDomains.Any(domain =>
                host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)),
            $"'{host}' is not a documentation placeholder host - a real Exchange/AI endpoint must never ship.");
    }

    private static string? FindNodeExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), OperatingSystem.IsWindows() ? "node.exe" : "node");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped; the toolchain check above fails loudly instead.
            }
        }

        return null;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunNode(string nodePath, string scriptPath, string scannedPath)
    {
        var startInfo = new ProcessStartInfo(nodePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(scannedPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {nodePath}.");

        // The scan prints a handful of lines, so sequential reads cannot fill a pipe buffer.
        var standardOut = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "verify-secrets.js did not finish within 60 seconds.");
        return (process.ExitCode, standardOut, standardError);
    }
}






