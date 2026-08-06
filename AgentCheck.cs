namespace ReviewFixLoop;

/// <summary>
/// Best-effort check that Codex is wired up for a repository.
/// A normal OAuth token cannot list App installations (that needs an App JWT or the `admin:org`
/// scope), so this looks for past Codex activity in the repository instead.
/// Kiro is deliberately not checked: it only pushes commits onto feature branches, which leaves
/// no repository-wide trace, so a missing Kiro surfaces through the kiro-timeout instead.
/// </summary>
internal static class AgentCheck
{
    public const string CodexConnectorUrl = "https://chatgpt.com/codex/cloud/settings/connectors";
    public const string KiroAgentUrl = "https://app.kiro.dev/settings/agent";

    public static async Task WarnIfCodexMissingAsync(PrRef pr, Action<string> log, CancellationToken ct)
    {
        // Null means the check itself failed, so an API hiccup never looks like a missing App.
        if (await HasCodexActivityAsync(pr, ct) == false)
            log($"No Codex activity found in {pr.Slug}. If `@codex review` is never picked up, "
                + $"connect the repository at {CodexConnectorUrl}");
    }

    private static async Task<bool?> HasCodexActivityAsync(PrRef pr, CancellationToken ct)
    {
        var query = Uri.EscapeDataString($"repo:{pr.Slug} commenter:{Signals.CodexBotLogin}");
        var r = await Gh.RunWithRetryAsync(
            ["api", $"search/issues?q={query}&per_page=1", "--jq", ".total_count"], rateLimitOnly: false, ct);

        return r.Ok && int.TryParse(r.StdOut, out var count) ? count > 0 : null;
    }
}
