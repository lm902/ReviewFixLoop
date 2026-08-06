using ReviewFixLoop;
using Xunit;

namespace ReviewFixLoop.Tests;

public class LoopPolicyTests
{
    private const string Head = "abc1234def5678900011122233344455566677";
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);

    private static TimelineSignal Result(int minute, bool clean, string commit = Head) =>
        new(SignalKind.CodexResult, T0.AddMinutes(minute),
            $"{(clean ? "Didn't find any major issues." : "Findings: fix line 12.")}\n\n**Reviewed commit:** `{commit[..7]}`");

    private static TimelineSignal Trigger(SignalKind kind, int minute) =>
        new(kind, T0.AddMinutes(minute), kind == SignalKind.CodexTrigger ? "@codex review" : "/kiro all");

    private static PrSnapshot Snapshot(
        TimelineSignal? result = null,
        TimelineSignal? codexTrigger = null,
        TimelineSignal? kiroTrigger = null,
        string state = "OPEN",
        bool bodyMentionsCodex = false,
        int codexTriggerCount = 0) =>
        new(Head, state, bodyMentionsCodex, T0, result, codexTrigger, kiroTrigger, codexTriggerCount);

    [Fact]
    public void ClosedPrStopsImmediately() =>
        Assert.Equal(LoopAction.PrClosed, LoopPolicy.Decide(Snapshot(state: "MERGED")));

    [Fact]
    public void CleanResultOnHeadApproves() =>
        Assert.Equal(LoopAction.Approved, LoopPolicy.Decide(Snapshot(result: Result(10, clean: true))));

    [Fact]
    public void CleanResultOnStaleCommitTriggersAnotherReview()
    {
        var stale = Result(10, clean: true, commit: "9999999aaaabbbbccccddddeeeeffff00001111");
        Assert.Equal(LoopAction.TriggerCodex, LoopPolicy.Decide(Snapshot(result: stale)));
    }

    [Fact]
    public void EmptyTimelineTriggersFirstReview() =>
        Assert.Equal(LoopAction.TriggerCodex, LoopPolicy.Decide(Snapshot()));

    [Fact]
    public void PrBodyMentioningCodexSkipsTheFirstTrigger() =>
        Assert.Equal(LoopAction.WaitForCodex, LoopPolicy.Decide(Snapshot(bodyMentionsCodex: true)));

    [Fact]
    public void FindingsTriggerKiro()
    {
        var s = Snapshot(result: Result(20, clean: false), codexTrigger: Trigger(SignalKind.CodexTrigger, 5));
        Assert.Equal(LoopAction.TriggerKiro, LoopPolicy.Decide(s));
    }

    [Fact]
    public void KiroAlreadyTriggeredAfterFindingsWaits()
    {
        var s = Snapshot(
            result: Result(20, clean: false),
            codexTrigger: Trigger(SignalKind.CodexTrigger, 5),
            kiroTrigger: Trigger(SignalKind.KiroTrigger, 25));
        Assert.Equal(LoopAction.WaitForKiro, LoopPolicy.Decide(s));
    }

    [Fact]
    public void NewestCodexTriggerWaitsForCodex()
    {
        var s = Snapshot(
            result: Result(10, clean: false),
            codexTrigger: Trigger(SignalKind.CodexTrigger, 40),
            kiroTrigger: Trigger(SignalKind.KiroTrigger, 20));
        Assert.Equal(LoopAction.WaitForCodex, LoopPolicy.Decide(s));
    }

    [Fact]
    public void StaleCleanResultAfterKiroStillTriggersReview()
    {
        var stale = Result(30, clean: true, commit: "9999999aaaabbbbccccddddeeeeffff00001111");
        var s = Snapshot(result: stale, kiroTrigger: Trigger(SignalKind.KiroTrigger, 20));
        Assert.Equal(LoopAction.TriggerCodex, LoopPolicy.Decide(s));
    }
}
