namespace ReviewFixLoop;

internal enum LoopOutcome { Approved, PrClosed, Timeout, KiroStalled, MaxRounds }

internal sealed record LoopOptions(
    TimeSpan InitialDelay,
    TimeSpan PollInterval,
    TimeSpan SilenceWindow,
    TimeSpan RoundTimeout,
    TimeSpan KiroTimeout,
    int MaxRounds,
    bool DryRun,
    bool Verbose);

/// <summary>What to do next, derived from a snapshot alone. Unrelated authors never reach here.</summary>
internal enum LoopAction { Approved, PrClosed, TriggerCodex, TriggerKiro, WaitForCodex, WaitForKiro }

internal static class LoopPolicy
{
    public static LoopAction Decide(PrSnapshot s)
    {
        if (!s.IsOpen) return LoopAction.PrClosed;

        var result = s.LastCodexResult;
        if (result is not null && result.IsClean && Signals.MatchesHead(result.ReviewedCommit, s.HeadSha))
            return LoopAction.Approved;

        var newest = s.Newest;
        if (newest is null)
            return s.BodyMentionsCodex ? LoopAction.WaitForCodex : LoopAction.TriggerCodex;

        return newest.Kind switch
        {
            // A clean-but-stale verdict needs a fresh review of the newer commits.
            SignalKind.CodexResult when newest.IsClean => LoopAction.TriggerCodex,
            SignalKind.CodexResult => LoopAction.TriggerKiro,
            SignalKind.CodexTrigger => LoopAction.WaitForCodex,
            SignalKind.KiroTrigger => LoopAction.WaitForKiro,
            _ => LoopAction.WaitForCodex,
        };
    }
}

internal sealed class ReviewLoop(PrRef pr, LoopOptions options, Func<CancellationToken, Task<PrSnapshot>> fetch)
{
    private const string FirstReviewBody =
        "Requesting an automated review of this pull request.\n\n@codex review";

    private const string ReReviewBody =
        "New commits have landed since the last review. Please take another look.\n\n@codex review";

    private const string KiroBody =
        "Codex reported findings above. Please address all of them.\n\n/kiro all";

    public async Task<LoopOutcome> RunAsync(CancellationToken ct)
    {
        while (true)
        {
            var snapshot = await fetch(ct);
            var action = LoopPolicy.Decide(snapshot);
            Log($"state={action} head={Short(snapshot.HeadSha)} rounds={snapshot.CodexTriggerCount}/{options.MaxRounds}");

            switch (action)
            {
                case LoopAction.Approved:
                    return LoopOutcome.Approved;

                case LoopAction.PrClosed:
                    Log($"{pr} is {snapshot.State}, nothing to drive.");
                    return LoopOutcome.PrClosed;

                case LoopAction.TriggerCodex:
                    if (snapshot.CodexTriggerCount >= options.MaxRounds)
                    {
                        Log($"Reached the {options.MaxRounds}-round limit without a clean review.");
                        return LoopOutcome.MaxRounds;
                    }
                    await PostAsync(snapshot.CodexTriggerCount == 0 ? FirstReviewBody : ReReviewBody, ct);
                    break;

                case LoopAction.TriggerKiro:
                    await PostAsync(KiroBody, ct);
                    break;

                case LoopAction.WaitForCodex:
                {
                    var outcome = await WaitForCodexAsync(snapshot, ct);
                    if (outcome is not null) return outcome.Value;
                    break;
                }

                case LoopAction.WaitForKiro:
                {
                    var outcome = await WaitForKiroAsync(snapshot, ct);
                    if (outcome is not null) return outcome.Value;
                    break;
                }
            }
        }
    }

    private async Task<LoopOutcome?> WaitForCodexAsync(PrSnapshot snapshot, CancellationToken ct)
    {
        var since = snapshot.LastCodexTrigger?.At ?? DateTimeOffset.UtcNow;
        var deadline = DateTimeOffset.UtcNow + options.RoundTimeout;
        await DelayUntilAsync(since + options.InitialDelay, ct);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var current = await PollAsync(ct);
            if (current is null)
            {
                await Task.Delay(options.PollInterval, ct);
                continue;
            }
            if (!current.IsOpen) return LoopOutcome.PrClosed;

            var result = current.LastCodexResult;
            if (result is not null && result.At > since)
            {
                Log(result.IsClean ? "Codex reported no major issues." : "Codex reported findings.");
                return null;
            }

            Log($"waiting for Codex, {Remaining(deadline)} left");
            await Task.Delay(options.PollInterval, ct);
        }

        Log($"No Codex result within {Fmt(options.RoundTimeout)}.");
        return LoopOutcome.Timeout;
    }

    private async Task<LoopOutcome?> WaitForKiroAsync(PrSnapshot snapshot, CancellationToken ct)
    {
        var since = snapshot.LastKiroTrigger?.At ?? DateTimeOffset.UtcNow;
        var deadline = DateTimeOffset.UtcNow + options.KiroTimeout;
        await DelayUntilAsync(since + options.InitialDelay, ct);

        while (true)
        {
            var current = await PollAsync(ct);
            if (current is null)
            {
                if (DateTimeOffset.UtcNow >= deadline) return LoopOutcome.KiroStalled;
                await Task.Delay(options.PollInterval, ct);
                continue;
            }
            if (!current.IsOpen) return LoopOutcome.PrClosed;

            var commitAt = current.LastCommitAt;
            if (commitAt > since)
            {
                var quiet = DateTimeOffset.UtcNow - commitAt.Value;
                if (quiet >= options.SilenceWindow)
                {
                    Log($"New commits settled ({Fmt(quiet)} quiet), requesting the next review.");
                    return null;
                }
                Log($"New commit {Fmt(quiet)} ago, waiting for {Fmt(options.SilenceWindow)} of quiet");
            }
            else
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    Log($"No commit from kiro-agent within {Fmt(options.KiroTimeout)}.");
                    return LoopOutcome.KiroStalled;
                }
                Log($"waiting for kiro-agent commits, {Remaining(deadline)} left");
            }

            await Task.Delay(options.PollInterval, ct);
        }
    }

    /// <summary>A fetch failure mid-wait is not fatal; the next poll can succeed.</summary>
    private async Task<PrSnapshot?> PollAsync(CancellationToken ct)
    {
        try
        {
            return await fetch(ct);
        }
        catch (GhException ex)
        {
            Log($"poll failed, will retry: {ex.Message}");
            return null;
        }
    }

    private async Task PostAsync(string body, CancellationToken ct)
    {
        if (options.DryRun)
        {
            Log($"[dry-run] would post:\n{body}");
            // Without a real comment the state never advances, so stop here.
            throw new DryRunStop();
        }

        var url = await Gh.PostIssueCommentAsync(pr, body, ct);
        Log($"posted {body.Split('\n')[^1].Trim()} -> {url}");
    }

    private async Task DelayUntilAsync(DateTimeOffset target, CancellationToken ct)
    {
        var delay = target - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero) return;
        Log($"first check in {Fmt(delay)}");
        await Task.Delay(delay, ct);
    }

    private static string Remaining(DateTimeOffset deadline) =>
        Fmt(deadline - DateTimeOffset.UtcNow);

    private static string Fmt(TimeSpan t) =>
        t <= TimeSpan.Zero ? "0s" : $"{(int)t.TotalMinutes}m{t.Seconds:00}s";

    private static string Short(string sha) =>
        sha.Length > 7 ? sha[..7] : sha;

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}

internal sealed class DryRunStop : Exception;
