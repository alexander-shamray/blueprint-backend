# /dispatch spike — 2026-11-01

Task 0 of docs/superpowers/plans/2026-11-01-dispatch.md.

## Decision gates

- Needs a grant: **none**. Task 1 is skipped; the PR body says so.
- Weekly figure: **none cheap for a script**. Task 4 ships the "not read" path
  only; the dispatcher's own interactive session still receives Token
  Weather's pause notice at 1% (ruling R7).
- Token Weather pause in a headless session: **mod loads, pause untested**.

## Run

`claude -p --output-format json --model sonnet`, prompt on stdin
`/ship land the /dispatch design spec and its implementation plan
(docs/superpowers only)`, cwd `.claude/worktrees/dispatch` (branch
`docs/dispatch-design`, two unpushed commits). Runner:
scratchpad `spike_run.py`; envelope in `artifacts/dispatch/spike-session.json`.

- exit 0, 937 s wall, `subtype: success`, `terminal_reason: completed`,
  `is_error: false`, `num_turns: 9`, `total_cost_usd: 1.886`.
- PR #1687 opened, two review rounds (plan rule: full pass 5 raised/5 fixed,
  recheck 2 raised/2 fixed), six required checks green, landed by
  `gh-pr-merge.sh 1687 d84f1368f…`, merge commit `89994113e`, `main`
  fast-forwarded and contains it.
- **The session woke on its own background CI wait**: it ended a turn at
  09:35 with `gh pr checks --watch` in the background, and the envelope's
  `origin` is `{"kind": "task-notification", "producer": "session-task"}`.
  A headless session does wait on CI and resume; spec §3's reason for
  sessions over subagents holds.
- `subagent_stats`: 2 spawned (`branch-reviewer`), 2 completed, 0 refused.

## Prompts

`permission_denials: []`. Why none: the user settings' `defaultMode` is
`auto`, which a `claude -p` session inherits; the repository settings set
none, and `/ship`'s frontmatter grants auto-approve its helpers. A call the
auto classifier refuses would surface as a denial in `permission_denials`
(and an `is_error` tool result), which `result` classifies as a hand-back.

Every `is_error: true` tool result in the transcript and its subagents':

| Call | Text | Class |
|---|---|---|
| `ExitWorktree` (step 6) | "No-op: there is no active EnterWorktree session to exit" | removed by `--dispatched` (spec §6.2) |

Worktree teardown: `git-worktree-remove.sh` deregistered the worktree and
could not delete the folder, the session's own cwd ("Permission denied");
the folder was left holding only `.remember/`. Removed by `--dispatched`:
the dispatcher removes the worktree from the main checkout (spec §6.2).

Task-list write: the nested `/ship` step 6 deleted the *In progress* row
itself — the write `--dispatched` skips.

**Code path, measured the same day** (owner asked): headless `/ship
#S3-1681 …` on Opus, started from the main checkout on a clean `main`, so
`/branch` forked and entered its own worktree inside the `-p` session. Exit 0,
2126 s, `subtype: success`, `num_turns: 11`, `total_cost_usd: 7.13`,
`permission_denials: []`; `subagent_stats` 5 spawned (3 general-purpose —
the dispatched `/validate-blueprint` passes — and 2 `branch-reviewer`), 5
completed. It ran `dotnet` builds and tests (`Common.Web.Tests` 384,
`Gateway.Api.Tests` 123), `dotnet format`, the comment gate, two review
rounds (1 low raised and fixed, then clean), waited on 53 CI checks, merged
PR #1688 at `1a4576297`, removed its worktree (it had entered it with
`EnterWorktree`, so `ExitWorktree` worked and the removal succeeded), and
deleted its task-list row. **No grant is needed for a code group either.**
One side effect: the executor wrote its final reply in Russian, from the
owner's auto-memory, which every session loads — the result file, not the
reply, is what the dispatcher reads.

**Formerly not measured** (ruling R8): a code change. This PR was docs-only, so no
`dotnet build`/`dotnet test`, no Docker, no dispatched `/validate-blueprint`
ran. Under `auto` they are classifier decisions, not grants; the first code
group in Task 10's acceptance run is where they are measured, and a denial
there is a hand-back, never a widened grant at night.

## Weekly figure

Not in the `-p` envelope (its keys: `usage`, `modelUsage`, `total_cost_usd`,
`permission_denials`, `subagent_stats`, … — no rate-limit field), not in the
transcript as data, not in any file under `~/.claude/`. Token Weather reads it
through the mod API `$.session.usage().rateLimits` (`SessionRateLimit`:
`kind` `seven_day`, `percentUsed`, `resetsAt`), available only inside a
running session to a mod. No cheap script source: spec §8's usage stop is
dropped for the helper (owner's decision), and the report says "not read".

## Token Weather pause

The mod loads in a `-p` session — its system-prompt line ("the Token Weather
plugin shows the line above the prompt") is in the headless transcript. Its
pause fires only at 1% left, which this run was nowhere near, so whether the
pause notice reaches a headless turn is untested.

## Rate-limit text

Not observed (no limit was hit). `LIMIT_PHRASES` / `RESET_PATTERN` in Task 3
cannot use the spike's exact text; they pin the envelope's structured fields
instead (`is_error`, `api_error_status`, `subtype`) with a fixture built from
the SDK's documented `rate_limit` error kind (ruling R9).
