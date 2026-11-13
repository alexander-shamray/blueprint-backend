# The locality gate

**The claim: a pull request's diff sits inside the change class its body
declares, and inside the touch set it declares, and leaves no path in the
tree that the tree's own `.gitignore` excludes.**
[`docs/change-locality.md`](../../docs/change-locality.md) §3 is the rule
for the first two; this gate is §3 enforced.

## What it reads

- The pull request body's `| Class |` and `| Touch set |` rows. A body
  without exactly one of each — or with a class letter outside A–E, a
  repeated member, or prose where a path list should be — is refused, not
  judged, and the refusal names the row and never prints its content,
  because the author is not a trusted party.
- [`classes.yml`](classes.yml), the one place the class → tree-set map
  lives. The contract's table states each class in words and cites the file.
  It is read by a parser that accepts the one shape the file's header states
  and refuses the whole map on anything else, on the licence gate's
  stdlib-only terms.
- Every changed path, both ends of a rename included, from the paginated
  files endpoint, with GitHub's `changedFiles` count beside it: the endpoint
  stops at a ceiling however it is paginated, so a shorter list is refused as
  a prefix. A path that is not a plain path refuses the run, because a
  verdict with a line withheld reads as complete. Each entry's `status`
  rides along, so a removal is not read as a path the diff leaves behind.
- The checkout's `.gitignore` files, through `git check-ignore --no-index`
  run from the working directory, which in CI is the pull request's merge
  checkout. A user's global excludes file is not read; the checkout's own
  `.git/info/exclude` still is, since git has no switch that skips it.

## What it judges

**Every changed path twice**: against the declared class's set, and against
the declared touch set. The map cannot say "one service" and the row can — a
Catalog change that also edits Ordering is inside Class A's set and outside
its own row, and only the second check sees it. A `+`-joined class is the
union of its members' sets.

**Every path the diff leaves in the tree, against the ignore rules read
case-folded** as a Windows checkout reads them, because an ignored file is one
each checkout keeps for itself. A worktree has none of its own at the fork,
so a tracked copy is what it finds; `docs/harness-boundaries.md` names the
MCP approval files as the case. A deliberate change un-ignores the path in
the same pull request, where a reviewer sees the line move.

The glob dialect and the row grammar are `.claude/scripts/pr-locality.sh`'s,
so a row reads the same in the harness and in CI. The two are separate
implementations, and they must accept and refuse the same tokens. The
helper reads its own checkout's `classes.yml` too, where CI reads the base
branch tip's, so a review round's class verdict is this gate's only while the
branch's map matches that tip's: a pull request that edits it, or a branch
behind a base whose map has since moved, is judged in CI by the base copy;
`test_pr_helpers_backend.py` runs both on one payload.

The suite is negative cases with their positive controls, plus reads of the
shipped map, so the gate has been observed looking at the file CI hands it.

## How it runs

The `gates` job in [`gates.yml`](../workflows/gates.yml), which runs the
pull-request gates as steps of one job, while its `open` and `report` jobs,
which check nothing out and alone hold `checks: write`, open this one's check
run before it judges and complete it as the `locality-gate` check. It has no
path filter and runs on `edited`, on the closure gate's argument: half of what
it judges is the body, which is edited without a push. A pull request that
declares no class or touch set stands down only where Dependabot opened it and
every commit's author and committer are Dependabot's; the opener alone is not
enough, so a human commit on a Dependabot branch is judged. **The gate and the
map that judge are read out of the base commit**, so a pull request cannot
widen its own class. A base with no gate script has no gate to run, so the
gate neither runs nor posts, and the pull request that lands the gate is not
judged by it. The workflow file is still the branch's copy, as the closure
gate's is.
**The suite runs after the verdicts are fixed.** Test code can write to
`$GITHUB_ENV` or `$GITHUB_PATH` or replace a tool on the runner, and every
later step inherits that, so a suite ahead of the gate could choose its
verdict with no workflow edit. A later step cannot turn a failure
green, and `python -I` would leave a replaced `python`, `gh` or `jq` in place,
so the order is the control. `gates.yml` fixes each verdict as a job output
before its suites run; the suite's `TheShippedWorkflow` reads the narrower
order, every suite after the gate step, and not where the verdicts step sits.
A red suite fails the `gates` job rather than the `locality-gate` check.
`docs/testing.md` has the live invocation.
