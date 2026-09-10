namespace EmailAI.Tests;

/// <summary>
/// Deliberately failing probe test. It exists ONLY on the temporary branch
/// <c>qa/protection-final-test</c> to prove that a red CI job blocks the merge of a pull request
/// into <c>main</c>. It is never merged: the probe pull request is closed and the branch is
/// deleted after the verification, so this class never reaches <c>main</c>.
/// </summary>
public class QaProbeIntentionalFailure
{
    [Fact]
    public void IntentionalFailure_ProvesTheRequiredCheckBlocksTheMerge()
        => Assert.True(
            false,
            "QA probe: intentional failure - CI must be red and the pull request must stay blocked.");
}
