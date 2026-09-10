using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace EmailAI.Tests;

/// <summary>
/// The release version contract, verified by RUNNING the real release machinery against real
/// inputs - not by asserting strings in a fixture:
///
/// <list type="bullet">
/// <item><c>desktop/scripts/verify-release.js</c> - the gate the release workflow runs before the
/// build (tag vs <c>desktop/package.json</c>) and after it (installer name, checksum, installer
/// version metadata, release notes);</item>
/// <item><c>desktop/scripts/release-version.js</c> - the library the gate, the pipeline and the
/// notes generator share, so a local release and CI cannot drift;</item>
/// <item><c>desktop/scripts/release-notes.js</c> - the generator that makes the published release
/// body describe the version being released.</item>
/// </list>
///
/// The load-bearing case is the one that shipped a <c>v1.3.0</c> release carrying an installer and
/// notes for the previous version: a tag that disagrees with the package version, an installer
/// named for another version, a checksum that belongs to a different file and notes that describe
/// an earlier release must all fail the gate - and the machinery must not contain a hard-coded
/// version to fall back to.
///
/// The checks that invoke Node.js report a skip when Node.js is not installed (the release
/// pipeline itself requires Node.js, so a machine that packages a release always runs them).
/// </summary>
public class ReleaseVersionTests(ITestOutputHelper output)
{
    private static class Repo
    {
        public static string Root { get; } = FindRoot();

        public static string Read(string relativePath)
            => File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

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

    /// <summary>The authoritative application version, read the way the gate reads it.</summary>
    private static string PackageVersion()
    {
        using var package = JsonDocument.Parse(Repo.Read("desktop/package.json"));
        return package.RootElement.GetProperty("version").GetString()!;
    }

    private static string TagFor(string version) => "v" + version;

    /// <summary>Another well-formed version: the contract under test is "a different version".</summary>
    private static string OtherVersion()
    {
        var parts = PackageVersion().Split('.');
        var patch = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        return $"{parts[0]}.{parts[1]}.{patch + 1}";
    }

    private static string InstallerNameFor(string version)
    {
        using var package = JsonDocument.Parse(Repo.Read("desktop/package.json"));
        var template = package.RootElement.GetProperty("build").GetProperty("nsis")
            .GetProperty("artifactName").GetString()!;
        return template.Replace("${version}", version, StringComparison.Ordinal);
    }

    private static string Sha256Of(string file)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    private static string CreateTempDirectory(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"emailai-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string WritePlaceholderInstaller(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, new byte[2048]);
        return path;
    }

    private static void WriteChecksumFile(string installerPath, string hash)
        => File.WriteAllText(installerPath + ".sha256", $"{hash}  {Path.GetFileName(installerPath)}\r\n");

    // ---------------------------------------------------------------- the toolchain

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
                // A malformed PATH entry is skipped; the release toolchain check fails loudly instead.
            }
        }

        return null;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunTool(string scriptName, params string[] arguments)
    {
        var node = FindNodeExecutable()
            ?? throw new InvalidOperationException("Node.js was not found on PATH.");
        var startInfo = new ProcessStartInfo(node)
        {
            WorkingDirectory = Repo.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(Repo.Root, "desktop", "scripts", scriptName));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {node}.");

        var standardOut = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), $"{scriptName} did not finish within 120 seconds.");
        return (process.ExitCode, standardOut, standardError);
    }

    private bool TryNode()
    {
        if (FindNodeExecutable() is not null)
        {
            return true;
        }

        output.WriteLine(
            "Node.js was not found on PATH, so the release-gate behaviour checks were not run here. " +
            "The release pipeline requires Node.js and runs these checks on every release.");
        return false;
    }

    /// <summary>The identity gate for the current checkout (no packaging output involved).</summary>
    private (int ExitCode, string StdOut, string StdErr) RunIdentityGate(string tag)
        => RunTool("verify-release.js", "--tag", tag, "--allow-tag-not-at-head");

    private static string GitOutput(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Repo.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git.");
        var standardOut = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return process.ExitCode == 0 ? standardOut.Trim() : string.Empty;
    }

    /// <summary>A release tag that exists in this checkout but does not point at HEAD, if any.</summary>
    private static string? TagNotAtHead()
    {
        var head = GitOutput("rev-parse", "HEAD");
        if (head.Length == 0)
        {
            return null;
        }

        foreach (var tag in GitOutput("tag", "--list", "v*.*.*").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = tag.Trim();
            var commit = GitOutput("rev-parse", $"{trimmed}^{{commit}}");
            if (commit.Length > 0 && !string.Equals(commit, head, StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    // ------------------------------------------------------------- the identity gate

    [Fact]
    public void VersionGate_PassesWhenTheTagMatchesThePackageVersion()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var result = RunIdentityGate(TagFor(version));

        Assert.True(
            result.ExitCode == 0,
            $"the gate rejected a tag that matches the package version:{Environment.NewLine}{result.StdOut}{result.StdErr}");
        Assert.Contains("PASS: release gate", result.StdOut, StringComparison.Ordinal);
        Assert.Contains(
            $"[ok]   version identity: tag {TagFor(version)} = desktop/package.json {version}",
            result.StdOut,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VersionGate_FailsWhenTheTagAndThePackageVersionDisagree()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var other = OtherVersion();
        var result = RunIdentityGate(TagFor(other));

        Assert.Equal(1, result.ExitCode);

        // The failure names the expected version, the actual version and the source of each value.
        Assert.Contains("release version mismatch", result.StdErr, StringComparison.Ordinal);
        Assert.Contains($"expected version   {other}", result.StdErr, StringComparison.Ordinal);
        Assert.Contains($"actual version     {version}", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("desktop/package.json", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("desktop/package-lock.json", result.StdErr, StringComparison.Ordinal);

        // ... and it says how to fix it, and that nothing is renamed to fit a tag.
        Assert.Contains("land it through a pull request", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("never by renaming an artifact", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("FAIL: release gate", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionGate_FailsWhenTheTagIsNotAReleaseTag()
    {
        if (!TryNode())
        {
            return;
        }

        var result = RunIdentityGate("main");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is not a release tag", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("desktop/package.json", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionGate_FailsWhenTheTagDoesNotPointAtTheCommitBeingBuilt()
    {
        if (!TryNode())
        {
            return;
        }

        var tag = TagNotAtHead();
        if (tag is null)
        {
            output.WriteLine(
                "This checkout has no release tag pointing at another commit, so the tag/commit " +
                "check was not exercised here.");
            return;
        }

        var directory = CreateTempDirectory("release-tag");
        try
        {
            // The tag matches this fabricated package version, so the identity itself holds - only
            // the "built from the tagged commit" rule can fail.
            File.WriteAllText(
                Path.Combine(directory, "package.json"),
                Repo.Read("desktop/package.json")
                    .Replace($"\"version\": \"{PackageVersion()}\"", $"\"version\": \"{tag.TrimStart('v')}\"", StringComparison.Ordinal));

            var result = RunTool("verify-release.js", "--tag", tag, "--desktop", directory);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("points at commit", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ------------------------------------------------------------ the artifact gate

    [Fact]
    public void ArtifactGate_PassesForAnInstallerNamedForTheReleaseVersionAndItsOwnChecksum()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var directory = CreateTempDirectory("release-artifact");
        try
        {
            var installer = WritePlaceholderInstaller(directory, InstallerNameFor(version));
            WriteChecksumFile(installer, Sha256Of(installer));

            // --no-metadata: this placeholder is not a Windows installer. The metadata rule has its
            // own test below, and the pipeline applies it to the real installer on every release.
            var result = RunTool(
                "verify-release.js", "--tag", TagFor(version), "--dist", directory,
                "--allow-tag-not-at-head", "--no-metadata");

            Assert.True(result.ExitCode == 0, $"{result.StdOut}{result.StdErr}");
            Assert.Contains("PASS: release gate", result.StdOut, StringComparison.Ordinal);
            Assert.Contains($"artifact: {InstallerNameFor(version)}", result.StdOut, StringComparison.Ordinal);
            Assert.Contains($"checksum: sha256 = {Sha256Of(installer)}", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ArtifactGate_FailsOnAnInstallerNamedForAnotherVersion()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var stale = InstallerNameFor(OtherVersion());
        var directory = CreateTempDirectory("release-stale");
        try
        {
            var installer = WritePlaceholderInstaller(directory, stale);
            WriteChecksumFile(installer, Sha256Of(installer));

            var result = RunTool(
                "verify-release.js", "--tag", TagFor(version), "--dist", directory,
                "--allow-tag-not-at-head", "--no-metadata");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("must publish exactly one installer", result.StdErr, StringComparison.Ordinal);
            Assert.Contains(InstallerNameFor(version), result.StdErr, StringComparison.Ordinal);
            Assert.Contains(stale, result.StdErr, StringComparison.Ordinal);
            Assert.Contains("never renamed", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ArtifactGate_FailsWhenTheChecksumBelongsToAnotherFile()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var directory = CreateTempDirectory("release-checksum");
        try
        {
            var installer = WritePlaceholderInstaller(directory, InstallerNameFor(version));
            var wrongHash = new string('a', 64);
            WriteChecksumFile(installer, wrongHash);

            var result = RunTool(
                "verify-release.js", "--tag", TagFor(version), "--dist", directory,
                "--allow-tag-not-at-head", "--no-metadata");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("checksum mismatch", result.StdErr, StringComparison.Ordinal);
            Assert.Contains(Sha256Of(installer), result.StdErr, StringComparison.Ordinal);
            Assert.Contains(wrongHash, result.StdErr, StringComparison.Ordinal);
            Assert.Contains("exact installer file that is published", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ArtifactGate_FailsWhenTheChecksumFileIsMissing()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var directory = CreateTempDirectory("release-no-checksum");
        try
        {
            WritePlaceholderInstaller(directory, InstallerNameFor(version));

            var result = RunTool(
                "verify-release.js", "--tag", TagFor(version), "--dist", directory,
                "--allow-tag-not-at-head", "--no-metadata");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains($"{InstallerNameFor(version)}.sha256", result.StdErr, StringComparison.Ordinal);
            Assert.Contains("must never be published without the checksum", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ArtifactGate_FailsWhenTheInstallerCarriesNoVersionMetadata()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var directory = CreateTempDirectory("release-metadata");
        try
        {
            var installer = WritePlaceholderInstaller(directory, InstallerNameFor(version));
            WriteChecksumFile(installer, Sha256Of(installer));

            // The name and the checksum are right; only the version embedded in the file is missing
            // (it is a placeholder, not a real installer) - a renamed artifact is not a release.
            var result = RunTool(
                "verify-release.js", "--tag", TagFor(version), "--dist", directory,
                "--allow-tag-not-at-head");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("no version metadata could be read", result.StdErr, StringComparison.Ordinal);
            Assert.Contains(version, result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ------------------------------------------------------------- the release notes

    [Fact]
    public void ArtifactGate_FailsWhenTheReleaseNotesDescribeAnotherVersion()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var other = OtherVersion();
        var directory = CreateTempDirectory("release-notes-version");
        try
        {
            var installer = WritePlaceholderInstaller(directory, InstallerNameFor(version));
            WriteChecksumFile(installer, Sha256Of(installer));

            // The defect that shipped a v1.3.0 release whose body was the previous release's notes.
            var notesPath = Path.Combine(directory, "RELEASE-NOTES.md");
            File.WriteAllText(notesPath, $"# EmailAI {other} - Release Notes\n\n**{InstallerNameFor(other)}**\n");

            var result = RunTool(
                "verify-release.js", "--tag", TagFor(version), "--dist", directory, "--notes", notesPath,
                "--allow-tag-not-at-head", "--no-metadata");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("describes the wrong version", result.StdErr, StringComparison.Ordinal);
            Assert.Contains($"expected  EmailAI {version}", result.StdErr, StringComparison.Ordinal);
            Assert.Contains($"found     EmailAI {other}", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReleaseNotesGenerator_WritesNotesForTheReleaseVersionInstallerAndChecksum()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var other = OtherVersion();
        var directory = CreateTempDirectory("release-notes-generate");
        try
        {
            var installer = WritePlaceholderInstaller(directory, InstallerNameFor(version));
            WriteChecksumFile(installer, Sha256Of(installer));
            var notesPath = Path.Combine(directory, "RELEASE-NOTES.md");

            var generated = RunTool(
                "release-notes.js", "--tag", TagFor(version), "--installer", installer, "--out", notesPath);

            Assert.True(generated.ExitCode == 0, $"{generated.StdOut}{generated.StdErr}");

            var notes = File.ReadAllText(notesPath);
            Assert.Contains($"# EmailAI {version} - Release Notes", notes, StringComparison.Ordinal);
            Assert.Contains(InstallerNameFor(version), notes, StringComparison.Ordinal);
            Assert.Contains(Sha256Of(installer), notes, StringComparison.Ordinal);
            Assert.DoesNotContain($"EmailAI {other}", notes, StringComparison.Ordinal);

            // The generated notes and the artifact pass the same gate the release workflow runs.
            var gate = RunTool(
                "verify-release.js", "--tag", TagFor(version), "--dist", directory, "--notes", notesPath,
                "--allow-tag-not-at-head", "--no-metadata");

            Assert.True(gate.ExitCode == 0, $"{gate.StdOut}{gate.StdErr}");
            Assert.Contains("release notes: they describe", gate.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReleaseNotesGenerator_RefusesToWriteNotesWithoutTheInstaller()
    {
        if (!TryNode())
        {
            return;
        }

        var version = PackageVersion();
        var directory = CreateTempDirectory("release-notes-missing");
        try
        {
            var notesPath = Path.Combine(directory, "RELEASE-NOTES.md");
            var generated = RunTool(
                "release-notes.js", "--tag", TagFor(version),
                "--installer", Path.Combine(directory, InstallerNameFor(version)),
                "--out", notesPath);

            Assert.Equal(1, generated.ExitCode);
            Assert.Contains("does not exist", generated.StdErr, StringComparison.Ordinal);
            Assert.False(File.Exists(notesPath), "no notes file may be written when the installer is missing.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // --------------------------------------------------- no version fallback

    [Fact]
    public void ReleaseMachinery_ContainsNoHardCodedVersionFallback()
    {
        // A version literal inside the release machinery is what let a v1.3.0 release ship a 1.1.0
        // installer with 1.1.0 notes: the release version must come from desktop/package.json only.
        string[] machinery =
        [
            "desktop/scripts/release.js",
            "desktop/scripts/release-version.js",
            "desktop/scripts/verify-release.js",
            "desktop/scripts/release-notes.js",
            ".github/workflows/release.yml",
        ];

        foreach (var file in machinery)
        {
            var text = Repo.Read(file);
            foreach (Match match in Regex.Matches(text, @"(?<![\w.])\d+\.\d+\.\d+(?![\w.])"))
            {
                Assert.Fail(
                    $"{file} contains the version literal '{match.Value}': the release version must be " +
                    "read from desktop/package.json, never hard-coded (a stale literal is a fallback).");
            }
        }

        // ... and the version is resolved in one place, which every other script consumes.
        foreach (var consumer in new[]
        {
            "desktop/scripts/release.js",
            "desktop/scripts/verify-release.js",
            "desktop/scripts/release-notes.js",
        })
        {
            Assert.Contains("require('./release-version')", Repo.Read(consumer), StringComparison.Ordinal);
        }

        Assert.Contains(
            "desktop/scripts/release-version.js",
            Repo.Read(".ai/spec/15-release-distribution.md"),
            StringComparison.Ordinal);
    }
}






