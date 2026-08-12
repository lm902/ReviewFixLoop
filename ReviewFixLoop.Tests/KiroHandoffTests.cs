using ReviewFixLoop;
using Xunit;

namespace ReviewFixLoop.Tests;

public class KiroHandoffTests
{
    private const string Head = "abc1234def5678900011122233344455566677";
    private static readonly PrRef Pr = new("owner", "repo", 1);

    private static LoopOptions Fast(int maxRounds = 5) =>
        new(TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(300),
            maxRounds,
            DryRun: true,
            Verbose: false);

    /// <summary>Commits are not timeline signals, so a settled kiro fix must not re-enter WaitForKiro.</summary>
    [Fact]
    public async Task SettledKiroCommitsLeadToATrigger()
    {
        var now = DateTimeOffset.UtcNow;
        var kiro = new TimelineSignal(SignalKind.KiroTrigger, now.AddMinutes(-10), "/kiro all");
        var calls = 0;

        var loop = new ReviewLoop(Pr, Fast(), _ =>
        {
            calls++;
            Assert.True(calls < 50, "loop spun without posting a trigger");
            return Task.FromResult(new PrSnapshot(
                Head, "OPEN", false,
                LastCommitAt: now.AddMinutes(-5),
                LastCodexResult: null,
                LastCodexTrigger: null,
                LastKiroTrigger: kiro,
                CodexTriggerCount: 1));
        });

        await Assert.ThrowsAsync<DryRunStop>(() => loop.RunAsync(CancellationToken.None));
    }

    /// <summary>A trigger posted while kiro was running already covers the new commits.</summary>
    [Fact]
    public async Task ExistingTriggerAfterTheCommitsIsNotDuplicated()
    {
        var now = DateTimeOffset.UtcNow;
        var kiro = new TimelineSignal(SignalKind.KiroTrigger, now.AddMinutes(-10), "/kiro all");
        var manual = new TimelineSignal(SignalKind.CodexTrigger, now.AddMinutes(-1), "@codex review");

        var loop = new ReviewLoop(Pr, Fast() with { RoundTimeout = TimeSpan.FromMilliseconds(200) }, _ =>
            Task.FromResult(new PrSnapshot(
                Head, "OPEN", false,
                // Observed on PR #424: kiro's commits land, then a trigger is posted by hand.
                LastCommitAt: now.AddMinutes(-3),
                LastCodexResult: null,
                LastCodexTrigger: manual,
                LastKiroTrigger: kiro,
                CodexTriggerCount: 1)));

        // Waiting out the existing trigger is correct; posting another one is not.
        Assert.Equal(LoopOutcome.Timeout, await loop.RunAsync(CancellationToken.None));
    }

    /// <summary>The kiro handoff must also skip the trigger when one already covers the commits.</summary>
    [Fact]
    public async Task HandoffSkipsTheTriggerWhenOneAlreadyCoversTheCommits()
    {
        var now = DateTimeOffset.UtcNow;
        var kiro = new TimelineSignal(SignalKind.KiroTrigger, now.AddMinutes(-10), "/kiro all");
        var calls = 0;

        var loop = new ReviewLoop(Pr, Fast() with { RoundTimeout = TimeSpan.FromMilliseconds(200) }, _ =>
        {
            calls++;
            // First decision enters WaitForKiro; then a trigger appears after the commits.
            var manual = calls >= 2
                ? new TimelineSignal(SignalKind.CodexTrigger, now.AddMinutes(-1), "@codex review")
                : null;
            return Task.FromResult(new PrSnapshot(
                Head, "OPEN", false,
                LastCommitAt: now.AddMinutes(-3),
                LastCodexResult: null,
                LastCodexTrigger: manual,
                LastKiroTrigger: kiro,
                CodexTriggerCount: 1));
        });

        Assert.Equal(LoopOutcome.Timeout, await loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RoundLimitStopsTheHandoffInsteadOfSpinning()
    {
        var now = DateTimeOffset.UtcNow;
        var kiro = new TimelineSignal(SignalKind.KiroTrigger, now.AddMinutes(-10), "/kiro all");
        var calls = 0;

        var loop = new ReviewLoop(Pr, Fast(maxRounds: 0), _ =>
        {
            calls++;
            Assert.True(calls < 50, "loop spun instead of reporting the round limit");
            return Task.FromResult(new PrSnapshot(
                Head, "OPEN", false,
                LastCommitAt: now.AddMinutes(-5),
                LastCodexResult: null,
                LastCodexTrigger: null,
                LastKiroTrigger: kiro,
                CodexTriggerCount: 9));
        });

        Assert.Equal(LoopOutcome.MaxRounds, await loop.RunAsync(CancellationToken.None));
    }
}
