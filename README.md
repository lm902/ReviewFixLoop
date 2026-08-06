# reviewfixloop

Drives a single GitHub pull request through an unattended Codex review / kiro-agent fix
loop until Codex reports no major issues for the current head commit.

## Requirements

- .NET 10 SDK
- [GitHub CLI](https://cli.github.com/) authenticated via `gh auth login`

## Usage

```pwsh
reviewfixloop [pr] [options]
```

`[pr]` accepts a PR URL, `OWNER/REPO#123`, or a bare number. It is optional: when omitted,
the PR for the current branch is used, falling back to your single open PR in the
repository. If several of your PRs are open, they are listed so you can pick one.

```pwsh
reviewfixloop                                             # discover the PR
reviewfixloop https://github.com/OWNER/REPO/pull/123
reviewfixloop 123 --repo OWNER/REPO --dry-run
```

### Options

All durations are in minutes.

| Option | Default | Meaning |
| --- | --- | --- |
| `--repo OWNER/REPO` | current repo | Repository to look in |
| `--initial-delay` | 5 | Wait before the first poll after a trigger comment |
| `--poll-interval` | 2 | Interval between polls |
| `--silence-window` | 3 | Quiet time after a new commit before requesting review |
| `--max-rounds` | 5 | Maximum `@codex review` rounds |
| `--round-timeout` | 45 | Give up waiting for a Codex result |
| `--kiro-timeout` | 30 | Give up waiting for kiro-agent commits |
| `--dry-run` | off | Print the comment that would be posted, post nothing |

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Codex approved the current head commit |
| 1 | Unexpected error |
| 2 | `gh` not installed |
| 3 | `gh` not authenticated |
| 4 | Bad arguments, or the PR could not be read or discovered |
| 5 | PR is closed or merged |
| 6 | No Codex result within `--round-timeout` |
| 7 | No kiro-agent commit within `--kiro-timeout` |
| 8 | `--max-rounds` reached without a clean review |

## How the loop decides

State is derived from four signals in the PR timeline, not from "the last comment".
Comments by unrelated authors are ignored, so a human replying mid-loop cannot derail it.

1. Latest Codex **result** — a `chatgpt-codex-connector[bot]` comment carrying a
   `Reviewed commit:` marker. Other bot chatter (queued, failed, quota) is not a result.
2. Latest `@codex review` trigger.
3. Latest `/kiro all` trigger.
4. Latest commit timestamp.

Decision table, evaluated on a fresh snapshot each iteration:

| Condition | Action |
| --- | --- |
| PR not open | exit `PrClosed` |
| Clean result whose `Reviewed commit` matches head | exit `Approved` |
| No signals and PR body does not mention `@codex` | post `@codex review` |

The approval check runs before the round limit, so a PR that is already clean exits 0
even if it burned more rounds than `--max-rounds` allows.
| Newest signal is a clean result on an older commit | post `@codex review` |
| Newest signal is a result with findings | post `/kiro all` |
| Newest signal is `@codex review` | wait for a Codex result |
| Newest signal is `/kiro all` | wait for commits, then the silence window |

Approval is commit-scoped: a clean review earned at an older commit does not end the
loop. Codex results are collected from all three endpoints that can carry them —
`issues/<pr>/comments`, `pulls/<pr>/reviews`, and `pulls/<pr>/comments`.

Waiting happens in the foreground with a log line per poll. The first poll after a
trigger is delayed until `trigger time + --initial-delay`; if that moment already
passed the loop polls immediately.

## Publish

Native AOT produces a self-contained single-file executable with no runtime dependency.
It needs the MSVC linker (Visual Studio "Desktop development with C++" workload or the
standalone Build Tools with `VC.Tools.x86.x64` plus a Windows SDK).

```pwsh
dotnet publish -r win-x64 -c Release -o publish
```

Without the MSVC linker, fall back to a trimmed self-contained single file. It still needs
no installed runtime, but starts slower and is larger:

```pwsh
dotnet publish -r win-x64 -c Release -p:PublishAot=false -o publish
```

## Tests

```pwsh
dotnet test ReviewFixLoop.Tests/ReviewFixLoop.Tests.csproj
```
