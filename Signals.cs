using System.Text.RegularExpressions;

namespace ReviewFixLoop;

/// <summary>Pure classification of PR timeline entries. No I/O.</summary>
internal static partial class Signals
{
    public const string CodexBotLogin = "chatgpt-codex-connector[bot]";
    public const string CodexTrigger = "@codex review";
    public const string KiroTrigger = "/kiro all";

    // A Codex review result always carries this marker; other bot chatter does not.
    private const string ReviewedCommitMarker = "reviewed commit";

    private static readonly string[] CleanPhrases =
    [
        "didn't find any major issues",
        "found no major issues",
        "no major issues found",
    ];

    public static bool IsCodexAuthor(string? login) =>
        string.Equals(login, CodexBotLogin, StringComparison.OrdinalIgnoreCase);

    /// <summary>True only for an actual review verdict, not queue/failure/quota chatter.</summary>
    public static bool IsCodexResult(string? login, string? body) =>
        IsCodexAuthor(login) && Normalize(body).Contains(ReviewedCommitMarker, StringComparison.Ordinal);

    public static bool IsCleanApproval(string? body)
    {
        var text = Normalize(body);
        return CleanPhrases.Any(p => text.Contains(p, StringComparison.Ordinal));
    }

    public static string? ExtractReviewedCommit(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = ReviewedCommitRegex().Match(body);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Reviewed commit may be abbreviated, so match by prefix.</summary>
    public static bool MatchesHead(string? reviewedCommit, string? headSha) =>
        !string.IsNullOrEmpty(reviewedCommit)
        && !string.IsNullOrEmpty(headSha)
        && headSha.StartsWith(reviewedCommit, StringComparison.OrdinalIgnoreCase);

    public static bool IsCodexReviewTrigger(string? login, string? body) =>
        !IsCodexAuthor(login) && Normalize(body).Contains(CodexTrigger, StringComparison.Ordinal);

    public static bool IsKiroFixTrigger(string? login, string? body) =>
        !IsCodexAuthor(login) && Normalize(body).Contains(KiroTrigger, StringComparison.Ordinal);

    public static bool MentionsCodex(string? body) =>
        Normalize(body).Contains("@codex", StringComparison.Ordinal);

    // Smart quotes and casing vary between Codex renderings.
    private static string Normalize(string? body) =>
        string.IsNullOrEmpty(body) ? string.Empty : body.Replace('\u2019', '\'').Replace('\u2018', '\'').ToLowerInvariant();

    [GeneratedRegex(@"Reviewed\s+commit\s*:?\s*\**\s*[`\[]*\s*([0-9a-fA-F]{7,40})", RegexOptions.IgnoreCase)]
    private static partial Regex ReviewedCommitRegex();
}
