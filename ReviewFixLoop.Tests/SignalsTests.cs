using ReviewFixLoop;
using Xunit;

namespace ReviewFixLoop.Tests;

public class SignalsTests
{
    private const string CodexResultBody = """
        ## Findings
        Something is wrong on line 12.

        **Reviewed commit:** `abc1234`
        """;

    private const string CleanBody = """
        Didn't find any major issues.

        **Reviewed commit:** abc1234def
        """;

    [Fact]
    public void CodexResultRequiresReviewedCommitMarker()
    {
        Assert.True(Signals.IsCodexResult(Signals.CodexBotLogin, CodexResultBody));
        Assert.False(Signals.IsCodexResult(Signals.CodexBotLogin, "Starting review..."));
        Assert.False(Signals.IsCodexResult("someone", CodexResultBody));
    }

    [Theory]
    [InlineData("Didn't find any major issues.", true)]
    [InlineData("Didn\u2019t find any major issues.", true)]
    [InlineData("No major issues found in this diff.", true)]
    [InlineData("Found 3 issues that need attention.", false)]
    public void CleanApprovalMatchesKnownWordings(string body, bool expected) =>
        Assert.Equal(expected, Signals.IsCleanApproval(body));

    [Theory]
    [InlineData("**Reviewed commit:** `abc1234`", "abc1234")]
    [InlineData("Reviewed commit: abc1234def5678", "abc1234def5678")]
    [InlineData("no marker here", null)]
    public void ReviewedCommitIsExtracted(string body, string? expected) =>
        Assert.Equal(expected, Signals.ExtractReviewedCommit(body));

    [Fact]
    public void AbbreviatedReviewedCommitMatchesFullHead()
    {
        Assert.True(Signals.MatchesHead("abc1234", "abc1234def5678900011122233344455566677"));
        Assert.False(Signals.MatchesHead("abc1234", "def5678000111222333444555666777888999a"));
        Assert.False(Signals.MatchesHead(null, "abc1234"));
    }

    [Fact]
    public void TriggersAreIgnoredWhenEchoedByCodex()
    {
        Assert.True(Signals.IsCodexReviewTrigger("dev", "please look again\n\n@codex review"));
        Assert.False(Signals.IsCodexReviewTrigger(Signals.CodexBotLogin, "@codex review"));
        Assert.True(Signals.IsKiroFixTrigger("dev", "/kiro all"));
        Assert.False(Signals.IsKiroFixTrigger(Signals.CodexBotLogin, "/kiro all"));
    }

    [Fact]
    public void CleanResultIsAlsoAResult() =>
        Assert.True(Signals.IsCodexResult(Signals.CodexBotLogin, CleanBody));
}
