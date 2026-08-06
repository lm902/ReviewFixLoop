using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ReviewFixLoop;

internal sealed record GhResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Thin wrapper over the `gh` CLI. Arguments are passed as a list, never string-interpolated.</summary>
internal static class Gh
{
    /// <summary>Upper bound on a single rate-limit sleep, so a skewed reset time cannot stall the run.</summary>
    public static TimeSpan RateLimitCap { get; set; } = TimeSpan.FromMinutes(15);

    public static Action<string> Log { get; set; } = _ => { };

    /// <summary>
    /// Retries transient failures and waits out rate limits.
    /// Set <paramref name="rateLimitOnly"/> for non-idempotent calls: a rate-limited request never
    /// reached the resource, but a lost response to a POST may have, so retrying it could duplicate.
    /// </summary>
    public static async Task<GhResult> RunWithRetryAsync(IReadOnlyList<string> args, bool rateLimitOnly = false, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await RunAsync(args, ct);
            var failure = GhRetry.Classify(result);
            if (rateLimitOnly && failure == GhFailure.Transient) return result;

            if (failure is GhFailure.None or GhFailure.Fatal || attempt >= GhRetry.MaxAttempts)
            {
                if (failure is not (GhFailure.None or GhFailure.Fatal))
                    Log($"gh {args[0]} still failing after {attempt} attempts, giving up.");
                return result;
            }

            var delay = failure == GhFailure.RateLimited
                ? await RateLimitDelayAsync(ct)
                : GhRetry.BackoffFor(attempt);

            Log($"gh {args[0]} {(failure == GhFailure.RateLimited ? "rate limited" : "transient error")}, "
                + $"retrying in {Describe(delay)} (attempt {attempt + 1}/{GhRetry.MaxAttempts})");
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Asks GitHub when the limit resets; falls back to fixed backoff if that call also fails.</summary>
    private static async Task<TimeSpan> RateLimitDelayAsync(CancellationToken ct)
    {
        var probe = await RunAsync(["api", "rate_limit"], ct);
        if (probe.Ok)
        {
            try
            {
                var limit = JsonSerializer.Deserialize(probe.StdOut, GhJson.Default.GhRateLimit);
                if (limit?.Rate is { Reset: > 0 } rate)
                    return GhRetry.RateLimitDelay(rate.Reset, DateTimeOffset.UtcNow, RateLimitCap);
            }
            catch (JsonException)
            {
                // Fall through to fixed backoff.
            }
        }

        return TimeSpan.FromSeconds(60);
    }

    private static string Describe(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{Math.Max(1, (int)t.TotalSeconds)}s";

    public static async Task<GhResult> RunAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new GhResult(-1, string.Empty, ex.Message);
        }

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return new GhResult(proc.ExitCode, (await stdout).Trim(), (await stderr).Trim());
    }

    public static async Task<T?> ApiAsync<T>(string endpoint, JsonTypeInfo<T> typeInfo, bool paginate, CancellationToken ct = default)
    {
        var args = new List<string> { "api", endpoint, "-H", "Accept: application/vnd.github+json" };
        if (paginate) args.AddRange(["--paginate", "--slurp"]);

        var r = await RunWithRetryAsync(args, rateLimitOnly: false, ct);
        if (!r.Ok) throw new GhException($"gh api {endpoint} failed: {DescribeError(r)}");

        return JsonSerializer.Deserialize(r.StdOut, typeInfo);
    }

    /// <summary>Body goes through a temp file so multi-line markdown survives argument handling.</summary>
    public static async Task<string> PostIssueCommentAsync(PrRef pr, string body, CancellationToken ct = default)
    {
        var file = Path.Combine(Path.GetTempPath(), $"reviewfixloop-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(file, body, ct);
        try
        {
            var r = await RunWithRetryAsync(
            [
                "api", "-X", "POST",
                $"repos/{pr.Owner}/{pr.Repo}/issues/{pr.Number}/comments",
                "-F", $"body=@{file}",
                "--jq", ".html_url",
            ], rateLimitOnly: true, ct);
            if (!r.Ok) throw new GhException($"Failed to post comment: {DescribeError(r)}");
            return r.StdOut;
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static async Task<bool> IsInstalledAsync(CancellationToken ct = default) =>
        (await RunAsync(["--version"], ct)).Ok;

    public static async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) =>
        (await RunAsync(["auth", "status"], ct)).Ok;

    private static string DescribeError(GhResult r) =>
        string.IsNullOrEmpty(r.StdErr) ? $"exit {r.ExitCode} {r.StdOut}" : r.StdErr;
}

internal sealed class GhException(string message) : Exception(message);
