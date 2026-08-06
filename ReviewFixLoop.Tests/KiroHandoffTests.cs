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
