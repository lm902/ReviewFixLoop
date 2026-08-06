using System.Text.RegularExpressions;

namespace ReviewFixLoop;

internal enum GhFailure { None, Transient, RateLimited, Fatal }

/// <summary>Classifies `gh` failures and decides how long to back off. No I/O.</summary>
internal static partial class GhRetry
{
    public const int MaxAttempts = 5;

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(40),
    ];

    // Server-side faults and connection problems are worth another attempt; 4xx generally is not.
    private static readonly int[] TransientStatuses = [408, 500, 502, 503, 504, 520, 522, 524];

    public static GhFailure Classify(GhResult r)
    {
        if (r.Ok) return GhFailure.None;

        // ExitCode -1 means the process could not be started at all.
        if (r.ExitCode == -1) return GhFailure.Fatal;

        var status = ExtractStatus(r.StdErr) ?? ExtractStatus(r.StdOut);
        if (status is 429) return GhFailure.RateLimited;

        // A 403 is rate limiting only when the message says so; otherwise it is a permission error.
        if (status is 403 && MentionsRateLimit(r)) return GhFailure.RateLimited;

        if (status is null) return IsNetworkError(r.StdErr) ? GhFailure.Transient : GhFailure.Fatal;

        return TransientStatuses.Contains(status.Value) ? GhFailure.Transient : GhFailure.Fatal;
    }

    public static TimeSpan BackoffFor(int attempt) =>
        Backoff[Math.Clamp(attempt - 1, 0, Backoff.Length - 1)];

    /// <summary>Seconds until the limit resets, clamped so a bad clock cannot cause a huge sleep.</summary>
    public static TimeSpan RateLimitDelay(long resetUnixSeconds, DateTimeOffset now, TimeSpan cap)
    {
        var delay = DateTimeOffset.FromUnixTimeSeconds(resetUnixSeconds) - now;
        // GitHub resets on a whole second, so add a small margin to avoid retrying one tick early.
        delay += TimeSpan.FromSeconds(2);
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > cap ? cap : delay;
    }

    public static int? ExtractStatus(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = HttpStatusRegex().Match(text);
        if (!m.Success) return null;
        var digits = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return int.TryParse(digits, out var code) ? code : null;
    }

    private static bool MentionsRateLimit(GhResult r) =>
        RateLimitPhraseRegex().IsMatch(r.StdErr) || RateLimitPhraseRegex().IsMatch(r.StdOut);

    private static bool IsNetworkError(string stderr) =>
        NetworkErrorRegex().IsMatch(stderr);

    [GeneratedRegex(@"\(HTTP (\d{3})\)|""status"":\s*""?(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex HttpStatusRegex();

    [GeneratedRegex(@"rate limit|secondary rate|abuse detection", RegexOptions.IgnoreCase)]
    private static partial Regex RateLimitPhraseRegex();

    [GeneratedRegex(@"dial tcp|connection reset|connection refused|no such host|i/o timeout|EOF|TLS handshake|context deadline exceeded",
        RegexOptions.IgnoreCase)]
    private static partial Regex NetworkErrorRegex();
}
