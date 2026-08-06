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

    // Real bodies observed on Celayix/team-xpress-native#397.
    private const string FindingsOnlyBody = """

        ### 💡 Codex Review

        https://github.com/o/r/blob/f0f608e/package-lock.json#L116
        **<sub><sub>![P1 Badge](https://img.shields.io/badge/P1-orange?style=flat)</sub></sub>  Regenerate the lockfile**

        Fresh evidence after the final regeneration.
        """;

    private const string ReviewObjectBody = """

        ### 💡 Codex Review

        Here are some automated review suggestions for this pull request.

        **Reviewed commit:** `9961c8dcf6`
        """;

    private const string CleanIssueCommentBody = """
        Codex Review: Didn't find any major issues. You're on a roll.

        **Reviewed commit:** `c47a61a9ef`
        """;

    /// <summary>The findings-only format has no `Reviewed commit:`, so it must match on the heading.</summary>
    [Fact]
    public void FindingsWithoutAReviewedCommitStillCountAsAResult()
    {
        Assert.True(Signals.IsCodexResult(Signals.CodexBotLogin, FindingsOnlyBody));
        Assert.False(Signals.IsCleanApproval(FindingsOnlyBody));
        Assert.Null(Signals.ExtractReviewedCommit(FindingsOnlyBody));
    }

    [Fact]
    public void ReviewObjectFormatIsAResult()
    {
        Assert.True(Signals.IsCodexResult(Signals.CodexBotLogin, ReviewObjectBody));
        Assert.False(Signals.IsCleanApproval(ReviewObjectBody));
        Assert.Equal("9961c8dcf6", Signals.ExtractReviewedCommit(ReviewObjectBody));
    }

    [Fact]
    public void CleanIssueCommentFormatIsACleanResult()
    {
        Assert.True(Signals.IsCodexResult(Signals.CodexBotLogin, CleanIssueCommentBody));
        Assert.True(Signals.IsCleanApproval(CleanIssueCommentBody));
        Assert.Equal("c47a61a9ef", Signals.ExtractReviewedCommit(CleanIssueCommentBody));
    }

    [Theory]
    [InlineData("Codex is reviewing this pull request...")]
    [InlineData("Codex could not complete the review. Please try again.")]
    public void BotChatterIsNotAResult(string body) =>
        Assert.False(Signals.IsCodexResult(Signals.CodexBotLogin, body));
}
