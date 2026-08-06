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

        var r = await RunAsync(args, ct);
        if (!r.Ok) throw new GhException($"gh api {endpoint} failed: {Describe(r)}");

        return JsonSerializer.Deserialize(r.StdOut, typeInfo);
    }

    /// <summary>Body goes through a temp file so multi-line markdown survives argument handling.</summary>
    public static async Task<string> PostIssueCommentAsync(PrRef pr, string body, CancellationToken ct = default)
    {
        var file = Path.Combine(Path.GetTempPath(), $"reviewfixloop-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(file, body, ct);
        try
        {
            var r = await RunAsync(
            [
                "api", "-X", "POST",
                $"repos/{pr.Owner}/{pr.Repo}/issues/{pr.Number}/comments",
                "-F", $"body=@{file}",
                "--jq", ".html_url",
            ], ct);
            if (!r.Ok) throw new GhException($"Failed to post comment: {Describe(r)}");
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

    private static string Describe(GhResult r) =>
        string.IsNullOrEmpty(r.StdErr) ? $"exit {r.ExitCode} {r.StdOut}" : r.StdErr;
}

internal sealed class GhException(string message) : Exception(message);
