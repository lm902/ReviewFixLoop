using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReviewFixLoop;

internal sealed record PrRef(string Owner, string Repo, int Number)
{
    public override string ToString() => $"{Owner}/{Repo}#{Number}";
}

internal enum SignalKind { CodexResult, CodexTrigger, KiroTrigger }

internal sealed record TimelineSignal(SignalKind Kind, DateTimeOffset At, string Body)
{
    public bool IsClean => Kind == SignalKind.CodexResult && Signals.IsCleanApproval(Body);
    public string? ReviewedCommit => Signals.ExtractReviewedCommit(Body);
}

internal sealed record PrSnapshot(
    string HeadSha,
    string State,
    bool BodyMentionsCodex,
    DateTimeOffset? LastCommitAt,
    TimelineSignal? LastCodexResult,
    TimelineSignal? LastCodexTrigger,
    TimelineSignal? LastKiroTrigger,
    int CodexTriggerCount)
{
    public bool IsOpen => string.Equals(State, "OPEN", StringComparison.OrdinalIgnoreCase);

    public TimelineSignal? Newest =>
        new[] { LastCodexResult, LastCodexTrigger, LastKiroTrigger }
            .Where(s => s is not null)
            .MaxBy(s => s!.At);
}

internal static partial class PrLookup
{
    public static async Task<PrRef> ResolveAsync(string arg, string? repoOption, CancellationToken ct)
    {
        var url = PrUrlRegex().Match(arg);
        if (url.Success)
            return new PrRef(url.Groups[1].Value, url.Groups[2].Value, int.Parse(url.Groups[3].Value));

        var slug = SlugRegex().Match(arg);
        if (slug.Success)
            return new PrRef(slug.Groups[1].Value, slug.Groups[2].Value, int.Parse(slug.Groups[3].Value));

        if (!int.TryParse(arg.TrimStart('#'), out var number) || number <= 0)
            throw new GhException($"Cannot parse PR reference '{arg}'. Use a URL, OWNER/REPO#123, or a number.");

        var repo = repoOption ?? await CurrentRepoAsync(ct);
        var parts = repo.Split('/');
        if (parts.Length != 2)
            throw new GhException($"Invalid --repo value '{repo}'. Expected OWNER/REPO.");

        return new PrRef(parts[0], parts[1], number);
    }

    private static async Task<string> CurrentRepoAsync(CancellationToken ct)
    {
        var r = await Gh.RunAsync(["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"], ct);
        if (!r.Ok || string.IsNullOrEmpty(r.StdOut))
            throw new GhException("Cannot determine the repository. Pass --repo OWNER/REPO or run inside a git repo.");
        return r.StdOut;
    }

    [GeneratedRegex(@"^https?://[^/]*github\.com/([^/]+)/([^/]+)/pull/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PrUrlRegex();

    [GeneratedRegex(@"^([A-Za-z0-9._-]+)/([A-Za-z0-9._-]+)#(\d+)$")]
    private static partial Regex SlugRegex();
}

internal static class PrSnapshotFetcher
{
    public static async Task<PrSnapshot> FetchAsync(PrRef pr, CancellationToken ct)
    {
        var view = await ViewAsync(pr, ct);

        // Codex results can land on any of three endpoints; all must be merged.
        var issueComments = await Gh.ApiAsync<List<List<IssueComment>>>(
            $"repos/{pr.Owner}/{pr.Repo}/issues/{pr.Number}/comments?per_page=100", paginate: true, ct);
        var reviews = await Gh.ApiAsync<List<List<Review>>>(
            $"repos/{pr.Owner}/{pr.Repo}/pulls/{pr.Number}/reviews?per_page=100", paginate: true, ct);
        var reviewComments = await Gh.ApiAsync<List<List<IssueComment>>>(
            $"repos/{pr.Owner}/{pr.Repo}/pulls/{pr.Number}/comments?per_page=100", paginate: true, ct);
        var commits = await Gh.ApiAsync<List<List<Commit>>>(
            $"repos/{pr.Owner}/{pr.Repo}/pulls/{pr.Number}/commits?per_page=100", paginate: true, ct);

        var entries = Flatten(issueComments).Select(c => (c.User?.Login, c.Body, c.CreatedAt))
            .Concat(Flatten(reviewComments).Select(c => (c.User?.Login, c.Body, c.CreatedAt)))
            .Concat(Flatten(reviews).Select(r => (r.User?.Login, r.Body, r.SubmittedAt ?? default)))
            .Where(e => e.Item3 != default)
            .ToList();

        var signals = new List<TimelineSignal>();
        foreach (var (login, body, at) in entries)
        {
            if (Signals.IsCodexResult(login, body))
                signals.Add(new TimelineSignal(SignalKind.CodexResult, at.ToUniversalTime(), body ?? string.Empty));
            else if (Signals.IsCodexReviewTrigger(login, body))
                signals.Add(new TimelineSignal(SignalKind.CodexTrigger, at.ToUniversalTime(), body ?? string.Empty));
            else if (Signals.IsKiroFixTrigger(login, body))
                signals.Add(new TimelineSignal(SignalKind.KiroTrigger, at.ToUniversalTime(), body ?? string.Empty));
        }

        var lastCommitAt = Flatten(commits)
            .Select(c => c.Detail?.Committer?.Date ?? c.Detail?.Author?.Date)
            .Where(d => d.HasValue)
            .Select(d => d!.Value.ToUniversalTime())
            .DefaultIfEmpty()
            .Max();

        return new PrSnapshot(
            HeadSha: view.HeadRefOid ?? string.Empty,
            State: view.State ?? string.Empty,
            BodyMentionsCodex: Signals.MentionsCodex(view.Body),
            LastCommitAt: lastCommitAt == default ? null : lastCommitAt,
            LastCodexResult: Latest(signals, SignalKind.CodexResult),
            LastCodexTrigger: Latest(signals, SignalKind.CodexTrigger),
            LastKiroTrigger: Latest(signals, SignalKind.KiroTrigger),
            CodexTriggerCount: signals.Count(s => s.Kind == SignalKind.CodexTrigger));
    }

    private static TimelineSignal? Latest(List<TimelineSignal> signals, SignalKind kind) =>
        signals.Where(s => s.Kind == kind).MaxBy(s => s.At);

    // `--slurp` wraps each page in an array; a non-paginated fallback yields a single page.
    private static IEnumerable<T> Flatten<T>(List<List<T>>? pages) =>
        pages?.SelectMany(p => p ?? []) ?? [];

    private static async Task<PrView> ViewAsync(PrRef pr, CancellationToken ct)
    {
        var r = await Gh.RunAsync(
        [
            "pr", "view", pr.Number.ToString(),
            "--repo", $"{pr.Owner}/{pr.Repo}",
            "--json", "number,state,headRefOid,body,url,isDraft",
        ], ct);
        if (!r.Ok)
            throw new GhException($"Cannot read {pr}: {(string.IsNullOrEmpty(r.StdErr) ? r.StdOut : r.StdErr)}");

        return System.Text.Json.JsonSerializer.Deserialize<PrView>(r.StdOut, Gh.Json)
               ?? throw new GhException($"Empty response for {pr}.");
    }

    private sealed record PrView(int Number, string? State, string? HeadRefOid, string? Body, string? Url, bool IsDraft);

    private sealed record User(string? Login);

    private sealed record IssueComment(
        string? Body,
        User? User,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

    private sealed record Review(
        string? Body,
        User? User,
        [property: JsonPropertyName("submitted_at")] DateTimeOffset? SubmittedAt);

    private sealed record Commit([property: JsonPropertyName("commit")] CommitDetail? Detail);

    private sealed record CommitDetail(GitSignature? Author, GitSignature? Committer);

    private sealed record GitSignature(DateTimeOffset? Date);
}
