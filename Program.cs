using ReviewFixLoop;

const int ExitApproved = 0;
const int ExitError = 1;
const int ExitGhMissing = 2;
const int ExitNotAuthenticated = 3;
const int ExitBadArgs = 4;
const int ExitPrClosed = 5;
const int ExitTimeout = 6;
const int ExitKiroStalled = 7;
const int ExitMaxRounds = 8;

if (args.Any(a => a is "-h" or "--help"))
{
    PrintUsage();
    return ExitApproved;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Cancelling...");
    cts.Cancel();
};

try
{
    var cli = CliOptions.Parse(args);
    Gh.Log = m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
    Gh.RateLimitCap = cli.RateLimitCap;

    if (!await Gh.IsInstalledAsync(cts.Token))
    {
        Console.Error.WriteLine("GitHub CLI (gh) was not found. Install it from https://cli.github.com/ and retry.");
        return ExitGhMissing;
    }

    if (!await Gh.IsAuthenticatedAsync(cts.Token))
    {
        Console.Error.WriteLine("GitHub CLI is not authenticated. Run `gh auth login` and retry.");
        return ExitNotAuthenticated;
    }

    var pr = await PrLookup.ResolveAsync(cli.PrArg, cli.Repo, cts.Token);
    Console.WriteLine($"Driving {pr}{(cli.Options.DryRun ? " (dry run)" : string.Empty)}");
    await AgentCheck.WarnIfCodexMissingAsync(pr, m => Console.WriteLine($"warning: {m}"), cts.Token);

    var loop = new ReviewLoop(pr, cli.Options, ct => PrSnapshotFetcher.FetchAsync(pr, ct));
    var outcome = await loop.RunAsync(cts.Token);

    Console.WriteLine($"Result: {outcome}");
    return outcome switch
    {
        LoopOutcome.Approved => ExitApproved,
        LoopOutcome.PrClosed => ExitPrClosed,
        LoopOutcome.Timeout => ExitTimeout,
        LoopOutcome.KiroStalled => ExitKiroStalled,
        LoopOutcome.MaxRounds => ExitMaxRounds,
        _ => ExitError,
    };
}
catch (DryRunStop)
{
    Console.WriteLine("Dry run stopped before posting.");
    return ExitApproved;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return ExitError;
}
catch (CliException ex)
{
    Console.Error.WriteLine(ex.Message);
    PrintUsage();
    return ExitBadArgs;
}
catch (GhException ex)
{
    Console.Error.WriteLine(ex.Message);
    return ExitBadArgs;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    return ExitError;
}

static void PrintUsage() => Console.WriteLine("""
    reviewfixloop [pr] [options]

      [pr]                     PR URL, OWNER/REPO#123, or a number. When omitted, the PR
                               for the current branch is used, falling back to your single
                               open PR in the repository.

    Options (durations in minutes):
      --repo OWNER/REPO        Repository to look in. Defaults to the current git repo.
      --initial-delay <n>      Wait before the first poll after a trigger. Default 5.
      --poll-interval <n>      Interval between polls. Default 2.
      --silence-window <n>     Quiet time after a new commit before requesting review. Default 3.
      --max-rounds <n>         Extra @codex review rounds this run may add. Default 5.
                               Counted on top of rounds already on the PR. 0 posts nothing.
      --round-timeout <n>      Give up waiting for a Codex result. Default 45.
      --kiro-timeout <n>       Give up waiting for kiro-agent commits. Default 30.
      --rate-limit-cap <n>     Longest single wait for a GitHub rate limit reset. Default 15.
      --dry-run                Print the comment that would be posted, post nothing.
      --verbose                Verbose logging.
      -h, --help               Show this help.

    Exit codes: 0 approved, 1 error, 2 gh missing, 3 not authenticated, 4 bad args,
                5 PR closed, 6 timeout, 7 kiro stalled, 8 round limit reached.
    """);

namespace ReviewFixLoop
{
    internal sealed record CliOptions(string? PrArg, string? Repo, TimeSpan RateLimitCap, LoopOptions Options)
    {
        public static CliOptions Parse(string[] args)
        {
            string? prArg = null;
            string? repo = null;
            var rateLimitCap = 15.0;
            var initialDelay = 5.0;
            var pollInterval = 2.0;
            var silenceWindow = 3.0;
            var maxRounds = 5;
            var roundTimeout = 45.0;
            var kiroTimeout = 30.0;
            var dryRun = false;
            var verbose = false;

            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "--repo": repo = Next(args, ref i); break;
                    case "--initial-delay": initialDelay = Minutes(Next(args, ref i), a); break;
                    case "--poll-interval": pollInterval = Minutes(Next(args, ref i), a); break;
                    case "--silence-window": silenceWindow = Minutes(Next(args, ref i), a); break;
                    case "--round-timeout": roundTimeout = Minutes(Next(args, ref i), a); break;
                    case "--kiro-timeout": kiroTimeout = Minutes(Next(args, ref i), a); break;
                    case "--rate-limit-cap": rateLimitCap = Minutes(Next(args, ref i), a); break;
                    case "--max-rounds": maxRounds = Rounds(Next(args, ref i), a); break;
                    case "--dry-run": dryRun = true; break;
                    case "--verbose": verbose = true; break;
                    default:
                        if (a.StartsWith('-')) throw new CliException($"Unknown option '{a}'.");
                        if (prArg is not null) throw new CliException("Only one PR reference is supported.");
                        prArg = a;
                        break;
                }
            }

            if (pollInterval <= 0) throw new CliException("--poll-interval must be greater than 0.");

            return new CliOptions(prArg, repo, TimeSpan.FromMinutes(rateLimitCap), new LoopOptions(
                TimeSpan.FromMinutes(initialDelay),
                TimeSpan.FromMinutes(pollInterval),
                TimeSpan.FromMinutes(silenceWindow),
                TimeSpan.FromMinutes(roundTimeout),
                TimeSpan.FromMinutes(kiroTimeout),
                maxRounds,
                dryRun,
                verbose));
        }

        private static string Next(string[] args, ref int i) =>
            ++i < args.Length ? args[i] : throw new CliException($"Option '{args[i - 1]}' needs a value.");

        private static double Minutes(string value, string option) =>
            double.TryParse(value, out var m) && m >= 0 ? m : throw new CliException($"'{option}' needs a non-negative number of minutes.");

        // Zero is valid: observe the PR without posting anything new.
        private static int Rounds(string value, string option) =>
            int.TryParse(value, out var n) && n >= 0 ? n : throw new CliException($"'{option}' needs a non-negative integer.");
    }

    internal sealed class CliException(string message) : Exception(message);
}
