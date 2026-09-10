using System.Text.RegularExpressions;

namespace EmailAI.Tests;

/// <summary>
/// The repository-workflow contract: how a change is allowed to reach <c>main</c> and what the
/// pull-request pipeline must verify before it can. It is verified against the FILES that define
/// that workflow - <c>.github/workflows/ci.yml</c> (the pull-request gate), the release workflow
/// (which must stay packaging-only), <c>CONTRIBUTING.md</c>, the pull-request template and the
/// documents that state the protection policy.
///
/// The load-bearing assertion is <c>RequiredCheckName_IsTheCiJobNameAndIsDocumented</c>: the branch
/// ruleset requires the status check by NAME, so renaming the CI job without updating the
/// documentation (and therefore the ruleset) would silently make <c>main</c> unmergeable again -
/// exactly the fault this suite exists to prevent.
/// </summary>
public class RepositoryWorkflowTests
{
    /// <summary>The status check the <c>main - pull request workflow</c> ruleset requires.</summary>
    private const string RequiredCheckName = "Verify pull request";

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

    private static string CiWorkflow => Repo.Read(".github/workflows/ci.yml");

    private static string ReleaseWorkflow => Repo.Read(".github/workflows/release.yml");

    /// <summary>The text between two markers of a file (neither marker included).</summary>
    private static string Section(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startMarker}' is missing.");

        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{endMarker}' is missing after '{startMarker}'.");

        return text[start..end];
    }

    /// <summary>The workflow with its comment lines removed: the contract is about the steps.</summary>
    private static string Code(string yaml)
        => string.Join(
            Environment.NewLine,
            yaml.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    // ------------------------------------------------------------- the pull-request gate

    [Fact]
    public void CiWorkflow_GatesPullRequestsAndPushesToMain()
    {
        var ci = CiWorkflow;

        Assert.Contains("name: CI", ci, StringComparison.Ordinal);

        var triggers = Section(ci, "on:", "permissions:");
        Assert.Contains("pull_request:", triggers, StringComparison.Ordinal);
        Assert.Contains("push:", triggers, StringComparison.Ordinal);

        // Both triggers are pinned to main, and neither may fire on a tag: tags belong to the
        // release workflow.
        Assert.Equal(2, Regex.Matches(triggers, @"(?m)^\s+-\s+main\s*$").Count);
        Assert.DoesNotContain("tags:", triggers, StringComparison.Ordinal);

        // Verification only, so the run never needs write access.
        Assert.Contains("contents: read", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void CiWorkflow_RunsTheDocumentedVerificationInOrder()
    {
        var ci = CiWorkflow;

        // Ordered because the order is the failing behaviour: the cheap gates (toolchain, syntax,
        // scan) must fail before the expensive build and test.
        string[] steps =
        [
            "actions/checkout@v4",
            "actions/setup-dotnet@v4",
            "dotnet-version: '10.0.x'",
            "actions/setup-node@v4",
            "node-version: '22'",
            "run: npm ci",
            "node --check main.js",
            "node --check scripts/release.js",
            "node --check scripts/verify-secrets.js",
            "node scripts/verify-secrets.js --allow-development-config ../src ../.env.example",
            "dotnet restore EmailAI.slnx",
            "dotnet build EmailAI.slnx -c Release",
            "dotnet test EmailAI.slnx -c Release",
        ];

        var previous = -1;
        foreach (var step in steps)
        {
            var index = ci.IndexOf(step, StringComparison.Ordinal);
            Assert.True(index > previous, $"'{step}' is missing from ci.yml or runs out of order (index {index}).");
            previous = index;
        }

        Assert.Contains("runs-on: windows-latest", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void CiWorkflow_DoesNotPackageTheInstaller()
    {
        var ci = Code(CiWorkflow);

        // Packaging stays in the release pipeline: a pull request must not pay for NSIS, must not
        // upload artifacts and must not need release permissions.
        string[] packaging =
        [
            "npm run release", "electron-builder", "nsis", "--self-contained",
            "actions/upload-artifact", "softprops/action-gh-release", "contents: write",
        ];

        foreach (var step in packaging)
        {
            Assert.DoesNotContain(step, ci, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReleaseWorkflow_StaysTagAndDispatchOnly()
    {
        var release = ReleaseWorkflow;

        Assert.Contains("'v*.*.*'", release, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", release, StringComparison.Ordinal);
        Assert.Contains("npm run release", release, StringComparison.Ordinal);

        // The installer pipeline must never be triggered by a pull request or by a branch push.
        Assert.DoesNotContain("pull_request", release, StringComparison.Ordinal);
        Assert.DoesNotContain("branches:", release, StringComparison.Ordinal);
    }

    // ------------------------------------------- the check name the ruleset requires

    [Fact]
    public void RequiredCheckName_IsTheCiJobNameAndIsDocumented()
    {
        // GitHub reports an Actions check under the JOB name, and the ruleset requires that exact
        // string. One job, one name: a second job with the same name would make the required check
        // ambiguous.
        var jobs = Section(CiWorkflow, "jobs:", Environment.NewLine + Environment.NewLine);
        var jobNames = Regex.Matches(jobs, @"(?m)^    name:\s*(?<name>.+?)\s*$")
            .Select(match => match.Groups["name"].Value)
            .ToArray();

        Assert.True(jobNames.Length == 1, $"expected exactly one ci.yml job name, found: {string.Join(", ", jobNames)}");
        Assert.Equal(RequiredCheckName, jobNames[0]);

        // The name is part of the protected-branch contract, so every document that states the
        // policy must state the same name.
        foreach (var document in new[]
        {
            "README.md",
            "CONTRIBUTING.md",
            ".github/PULL_REQUEST_TEMPLATE.md",
            ".ai/service-context.md",
            ".ai/spec/14-testing-strategy.md",
            ".ai/spec/15-release-distribution.md",
        })
        {
            Assert.Contains(RequiredCheckName, Repo.Read(document), StringComparison.Ordinal);
        }
    }

    // -------------------------------------------------------------- the documented policy

    [Fact]
    public void Contributing_DocumentsTheProtectedBranchPolicy()
    {
        Assert.True(Repo.Exists("CONTRIBUTING.md"), "CONTRIBUTING.md is missing.");
        var contributing = Repo.Read("CONTRIBUTING.md").ToLowerInvariant();

        foreach (var rule in new[]
        {
            "fork", "feature branch", "pull request", "squash", "direct push", "force push",
            "delete", "pull request workflow", "main - integrity", "verify pull request",
        })
        {
            Assert.Contains(rule, contributing, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Readme_DocumentsTheContributorWorkflow()
    {
        var readme = Repo.Read("README.md");

        Assert.Contains("## Contributing", readme, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/ci.yml", readme, StringComparison.Ordinal);
        Assert.Contains("CONTRIBUTING.md", readme, StringComparison.Ordinal);
        Assert.Contains(RequiredCheckName, readme, StringComparison.Ordinal);
        Assert.Contains("squash", readme.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public void PullRequestTemplate_AsksForTheVerificationAndTheSecretGate()
    {
        Assert.True(Repo.Exists(".github/PULL_REQUEST_TEMPLATE.md"), "the pull-request template is missing.");
        var template = Repo.Read(".github/PULL_REQUEST_TEMPLATE.md");

        Assert.Contains(RequiredCheckName, template, StringComparison.Ordinal);
        Assert.Contains("dotnet test EmailAI.slnx -c Release", template, StringComparison.Ordinal);
        Assert.Contains("verify-secrets.js", template, StringComparison.Ordinal);
        Assert.Contains("secret", template.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Fact]
    public void SecretScan_IsAPullRequestGateAndAReleaseGate()
    {
        const string sourceTreeScan =
            "node scripts/verify-secrets.js --allow-development-config ../src ../.env.example";

        Assert.True(Repo.Exists("desktop/scripts/verify-secrets.js"));
        Assert.Contains(sourceTreeScan, CiWorkflow, StringComparison.Ordinal);
        Assert.Contains(sourceTreeScan, ReleaseWorkflow, StringComparison.Ordinal);
    }
}
