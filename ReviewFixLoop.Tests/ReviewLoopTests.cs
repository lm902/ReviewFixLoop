using ReviewFixLoop;
using Xunit;

namespace ReviewFixLoop.Tests;

public class ReviewLoopTests
{
    private const string Head = "abc1234def5678900011122233344455566677";
    private static readonly PrRef Pr = new("owner", "repo", 1);

    // Sub-second durations keep the real Task.Delay waits fast.
    private static LoopOptions Fast(int maxRounds = 5, double roundTimeoutMs = 300, double kiroTimeoutMs = 300) =>
        new(TimeSpan.Zero,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(roundTimeoutMs),
            TimeSpan.FromMilliseconds(kiroTimeoutMs),
            maxRounds,
            DryRun: false,
            Verbose: false);

    private static PrSnapshot Snapshot(
        TimelineSignal? result = null,
        TimelineSignal? codexTrigger = null,
        TimelineSignal? kiroTrigger = null,
        DateTimeOffset? lastCommitAt = null,
        string state = "OPEN",
        int codexTriggerCount = 0) =>
        new(Head, state, false, lastCommitAt, result, codexTrigger, kiroTrigger, codexTriggerCount);

    private static TimelineSignal CleanResult(DateTimeOffset at) =>
        new(SignalKind.CodexResult, at, $"Didn't find any major issues.\n\n**Reviewed commit:** `{Head[..7]}`");

    [Fact]
    public async Task WaitingForCodexTimesOut()
    {
        var trigger = new TimelineSignal(SignalKind.CodexTrigger, DateTimeOffset.UtcNow, "@codex review");
        var loop = new ReviewLoop(Pr, Fast(), _ => Task.FromResult(Snapshot(codexTrigger: trigger)));

        Assert.Equal(LoopOutcome.Timeout, await loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task KiroWithoutCommitsStalls()
    {
        var trigger = new TimelineSignal(SignalKind.KiroTrigger, DateTimeOffset.UtcNow, "/kiro all");
        var loop = new ReviewLoop(Pr, Fast(), _ => Task.FromResult(
            Snapshot(kiroTrigger: trigger, lastCommitAt: DateTimeOffset.UtcNow.AddHours(-1))));

        Assert.Equal(LoopOutcome.KiroStalled, await loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CodexResultArrivingWhileWaitingEndsTheWait()
    {
        var trigger = new TimelineSignal(SignalKind.CodexTrigger, DateTimeOffset.UtcNow, "@codex review");
        var calls = 0;
        var loop = new ReviewLoop(Pr, Fast(), _ =>
        {
            calls++;
            // First decision waits; the clean result then lands mid-wait.
            var result = calls >= 3 ? CleanResult(trigger.At.AddSeconds(1)) : null;
            return Task.FromResult(Snapshot(result: result, codexTrigger: trigger));
        });

        Assert.Equal(LoopOutcome.Approved, await loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RoundLimitStopsBeforePosting()
    {
        var loop = new ReviewLoop(Pr, Fast(maxRounds: 2), _ => Task.FromResult(Snapshot(codexTriggerCount: 2)));

        Assert.Equal(LoopOutcome.MaxRounds, await loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PrClosedDuringWaitStopsTheLoop()
    {
        var trigger = new TimelineSignal(SignalKind.CodexTrigger, DateTimeOffset.UtcNow, "@codex review");
        var calls = 0;
        var loop = new ReviewLoop(Pr, Fast(roundTimeoutMs: 5000), _ =>
        {
            calls++;
            var state = calls >= 2 ? "CLOSED" : "OPEN";
            return Task.FromResult(Snapshot(codexTrigger: trigger, state: state));
        });

        Assert.Equal(LoopOutcome.PrClosed, await loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DryRunStopsInsteadOfPosting()
    {
        var options = Fast() with { DryRun = true };
        var loop = new ReviewLoop(Pr, options, _ => Task.FromResult(Snapshot()));

        await Assert.ThrowsAsync<DryRunStop>(() => loop.RunAsync(CancellationToken.None));
    }
}
