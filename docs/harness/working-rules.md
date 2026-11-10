# Working rules

How a change is made in a repository on this harness: commits, pull
requests, review, CI, tests and gates, issues and the task list, and
managed files. `.harness/agent-rules.md` owns the rules every session loads;
this file owns these, read before the act each section names. A fact only
one target has — its change classes, its locality gate, its style guide —
is that target's, named in `docs/harness-local.md` *Documents*, and is
cited from here, never restated.

## Commits

- **Commit messages are semantic and present-tense** — `docs:`,
  `feat(<scope>):`, `fix:`, `chore:`, `refactor:`, `test:` — and the body
  argues the change. The subject names what changed in words a reviewer
  would search for — a file, a symbol, a behaviour — and leaves the
  aphorism to the body. The commit gate refuses a type outside that set.
- **A `Closes #n` in a commit body fires on merge whatever the description
  says**, so the description is reconciled to the commits and never the
  reverse.
- **Every commit carries `PR: #<n>`, and every intermediate commit
  builds.** A pull request lands by rebase, so the trailer is the only
  record of which pull request a commit on `main` came from; `/pr` adds it
  once the number exists, `/commit` writes it after, and the commit gate
  refuses a range without it. Reverting a pull request is
  `git-revert-pr.sh <n>`.
- **A round's touch set is the finding's own sites.** A `fix` commit
  closing a review finding names the finding in its subject and carries
  that finding's fix — the owner site and the sites the triage accepted
  with it — and nothing else; any other edit is a second commit, with its
  own finding.
- **Agents sharing one worktree commit by pathspec**: `git add <paths>`,
  then `git commit -- <paths>`, since they share one index and a plain
  commit takes what another agent staged. None resets, stashes or checks
  out a file it did not change, or amends once `HEAD` has moved past its
  commit.
- **`.github/comment-gate/` judges the lines a pull request adds, and a
  block it touches counts whole**, so a one-line edit to a long comment
  block makes the pull request own all of it. It reads committed state:
  run it after the commit, and amend the unpushed commit if it finds a
  block over its limit.

## Pull requests

- **A branch is updated for a conflict, never because another pull request
  merged.** `main` lands by rebase alone and a branch need not be up to
  date with it to merge, so rebase an open pull request only when GitHub
  reports it conflicting or refuses its rebase landing, or once the revert
  or fix for a check that belongs to `main` has landed (*CI*), and only a
  branch this session owns.
  `git-rebase-onto-main.sh <branch> start` is the
  update: it fetches `origin/main`, replays the branch onto it and
  publishes under a lease. Set `git config rerere.enabled true` once per
  clone. `/ship` step 6 merges after its own wait on the checks.
- **A pull request that is ready merges in the turn that establishes it
  is ready**, a Dependabot one aside, which the owner merges: every check
  settled green, the review ended, no blocking deny left open, and, for
  the one of a set serving one issue that carries the set's closing
  keyword, every other pull request of the set landed, so the issue closes
  only once the whole set has. Handing a ready pull request back with
  merge instructions makes the owner a queue step for a decision already
  made.
- **A Dependabot pull request is the one exception to the trailer and to
  the class row.** It declares no class and no touch set and carries no
  `PR:` trailer, so the gates that read them skip it, and a skipped step
  passes a required check. The owner merges it. The commit gate stands
  down only where every commit's author and committer are Dependabot's; it
  judges by the identities a commit records, not by who pushed it. Once it
  judges, every commit in the range must carry the trailer and the form,
  Dependabot's included, so the commits are rewritten or the branch
  replaced. With no trailer, `git-revert-pr.sh` cannot find its commit, so
  one is reverted with `git revert` on a branch of its own.
- **Landing a Dependabot pull request is the owner's, and has three
  catches.** `gh-pr-merge.sh` binds to the checked-out branch, so a
  session cannot land one from elsewhere; the owner's merge is `gh pr
  merge <n> --rebase --match-head-commit <oid>`; and where GitHub reports
  the branch conflicting, the update is a rebase request
  (`@dependabot rebase`), not a push to its branch, which can wait tens of
  minutes for its run, so a session asks, does other work and reads the
  head again, rather than polling.
- **The class and the touch set** a pull request declares are the change
  contract's that `docs/harness-local.md` *Documents* names; a target
  with none declares none.

## Review

- **Every pull request runs the review loop before it merges**, and an
  inline self-review does not replace a round (`.harness/agent-rules.md`,
  *Subagents, review rounds and plans*). A zero-review merge is never
  allowed.
- **Green gates say nothing about prose.** The gates read paths, diffs and
  code; none reads a sentence for meaning, so a specification change can
  be green everywhere and contradict its peers. The review is what reads
  it.
- **One owner per fact.** Every other mention cites the owner by symbol or
  file, never by value. `CLAUDE.md` is inside the rule too, and no gate reads
  what it says, so a rule stated there and argued in a config file moves in
  both. A restatement a change leaves untouched stays under *What counts as a
  finding*.
- **A round's prompt states the bar** from round 1, as *What counts as a
  finding* words it: a statement untrue of the system — the code
  contradicts it, or it says "all" where only some hold — is a finding;
  one incomplete but true is not. A reviewer not told so reports
  every omission, and each fix adds text to review.
- **A rule paragraph a fix rewrites is re-read whole** against the pull
  request's own commits before committing, not only the clause flagged:
  a rewrite that keeps a clause the last fix qualified is the next round's
  finding.
- **A criteria or method file the reviewer must apply is written out in
  full** under `artifacts/review/`, never as a pointer to the file: its
  profile holds no shell and paths under `.claude/` may be refused it, so
  it cannot be relied on to reach the original; a round that tolerated a
  pointer was luck.
- **A reply is not a resolution.** Resolve a review thread in the same act
  as the reply, and leave an `Ask` open on purpose.
- **A CodeQL alert is a defect in the pull request that raised it, and it
  is fixed there**: break the reported path at its source rather than
  policing the sink, or argue in the commit body why the path cannot be
  taken.

## What counts as a finding

**Two statements that cannot both be true**, or one untrue of the system;
never style taste. Prefer, beside the kinds this target's own surface
produces (`docs/harness-local.md` *Review examples*):

1. **Incomplete reconciliation**: a rule or fix this change states that
   the corpus still violates in the same change set.
2. **A file outside the declared touch set**, resolved as the change
   contract `docs/harness-local.md` *Documents* names says — a file
   outside the class's own trees by a narrower diff or a different class,
   any other by the contract's own route — never by a row widened
   silently. Where the
   target has no contract, or its locality check prints nothing or cannot
   run, say so and skip; never infer a class.

Never raise a statement that is incomplete but true, the style guide's
tabulated house styles, a stale restatement this branch left untouched
where the owner site is already correct, or a request that a true comment
keeping the guide's *Comments* rules be reworded, completed or expanded.

## CI

- **A check state is read after the current head's run settles.**
  `gh pr checks <n>` answers for the instant it ran, so a summary built on
  a pending listing claims a state nobody has seen. Wait on it as one
  background task (`.harness/agent-rules.md`, *Subagents, review rounds
  and plans*) and report what it settled to, naming the failing check.
- **A red check is fixed at once**, in its own commit, before anything
  else resumes — waiting on a review included. Fix the gate to match the
  failure only when the gate is what is wrong, and say which direction was
  taken. A red found while waiting on something else is found early, not
  at the merge. A check the pull request touched none of is `main`'s, below.
- **A red `main` is reverted first and fixed forward second.** The next
  pull request to land reverts the one that turned `main` red, by
  `git-revert-pr.sh <n>`; the fix forward is a pull request of its own
  after it. A red run that a rerun turns green is a flake: it is a comment
  on the flaky tests tracker, and an issue filed for the red `main`
  closes on that green run. That open issue, titled `main is red` or
  `main is red: <workflow>` and labelled `bug`, is the signal: `/ship` step 6
  reads it with `main-red-open.sh` and stops as on a red check, but for a run
  started as `/ship #<n>` to answer it. While it is open `/dispatch`'s `wake`
  names no group and its sync launches refuse, and on a night the rerun and the
  close are the owner's (`docs/commands/dispatch.md`, *A red main*).
- **A check red on a pull request that touched none of it belongs to
  `main`**, whether or not `main` ran red: a fix can land before `main` runs
  again, and `closure-gate`, `comment-gate`, `commit-gate` and
  `harness-drift` run on `pull_request` alone, so `main` never runs them.
  *None of it* means a fault outside what the pull request changed, so a
  gate's refusal of its own commits, body or added lines is its own to fix.
  A rerun comes first, and one that turns green is a flake, as above. Failing
  that, it waits for the revert or the fix, each a pull request of its own,
  and is not changed to make it pass. A rerun would then still read the old
  merge, since a `pull_request` run is pinned to the merge it started on, so
  it is updated through `git-rebase-onto-main.sh`, an update no conflict asked
  for.
- **Auto-merge waits on `main`'s required checks and nothing else**: not on
  the review, not on an open issue for a red `main`, and not on a branch
  being up to date. So a pull request is set to auto-merge only once its
  review has ended and no such issue is open. When one opens, whoever answers
  it, by a revert or by a comment on the flaky tests tracker, also has
  auto-merge turned off on every pull request that has it, since it would
  otherwise land through the issue. Nothing automates that, as nothing
  automates the revert, and the base settings deny a session `gh pr merge`,
  so `gh pr merge <n> --disable-auto` is the owner's. `/ship` does not set
  it (`/ship` step 6).

## Tests and gates

- **A guard's test asserts the distinguishing evidence** — the refusal's
  message, or the protected state unchanged — never the exit code alone:
  exit codes are a small vocabulary and guards are layered, so a deleted
  check with a green exit-code test is the shape that lets a defect land.
  A structural assertion reads code lines, never the raw file.
- **A new guard's test is proven by mutation**: delete the guard in a
  scratch copy and re-run; a test that stays green establishes nothing.
  For a text or regex guard, mutate with two or three paraphrases a person
  would plausibly write, not the original sentence put back, and check
  the guard passes on the correct tree first. A fixture asserts its own
  premise.
- **A verification's input is derived from the real artefact** — walk the
  checkout, ask git — never written down from what is expected, and the
  code under test runs against a tree nobody in the session constructed.
  Name the items rather than count them.
- **Run the touched suites before each commit**: the suites whose text
  names a path the change touches. The full suite is CI's, and a green CI
  run is the full-suite evidence; say so rather than claim a local run.
  A change to line endings or subprocess handling carries a
  Windows-shaped case in its own suite.
- **A test that puts an executable on `PATH` writes both forms**: a
  `<name>.cmd` and a `#!/bin/sh` `<name>` made executable, or CI's Linux
  runner finds the real one.
- **A fix to a script under `.claude/` lands with the case in its suite
  that failed before it**, and a script with no suite gets one before its
  second fix: a fix no case pins is one the next pull request makes again.

## Issues and the task list

- **The issue vocabulary**: kind is `security`, `bug`, `test-gap`,
  `enhancement` or `documentation`; area is the tree a finding is in, one
  to a finding — `harness` for anything under `.claude/`, its prose
  included, `documentation` for prose and samples elsewhere, `code` for the
  rest — with `test` beside `harness` or `code` for a test suite.
  `gh-issue-hand.sh` and `gh-issue-sweep.sh` take the area as their third
  argument and `gh-issue-mutation.sh` as its second; a label
  outside its set, `tracking`, `paused` or `flaky`, is added by
  `gh issue edit` after filing. A severity in the body and the label both
  say it. `documentation` is an issue's kind only on one that carries no
  other.
- **An issue's comments are read before it is judged unblocked**: the
  owner records live measurements that block an issue as comments.
- **A comment or review feed is read whole, never cut with `head` or
  `tail`.** One body runs to kilobytes, so a cut lands inside the first
  and ends mid-prose, which reads as complete: a pull request at a clean
  second round was once reported as owing its first. A REST read also
  stops at 30 items, and under `--paginate` a `--jq` filter runs once per
  page, so `.[-1]` there is not the newest nor `length` the count: write
  the whole feed, `gh api --paginate`, to a file, and take the newest or
  the count from that file.
- **The task list** is where `.harness/agent-rules.md`, *Working in a
  target*, puts it. **Where a row sits is a function of state**, so a session
  re-derives it at each event (work starts on an issue, a pull request opens,
  merges or closes, an issue is filed, closed, reopened, paused or unpaused,
  labelled or unlabelled `tracking`, work is abandoned) and writes the result
  in the same act:

  | State | Where |
  |---|---|
  | Open issue not labelled `tracking`, no work under way and no open pull request | *Remaining*, in its wave |
  | Open issue labelled `tracking`, not `paused`: it records work done elsewhere or a measurement over time, and no pull request implements it | *Tracking*, in its wave |
  | Open issue labelled `paused` | *Paused*, whatever else is true of it |
  | Work a session has taken up (about to be branched, or with a branch or a worktree), with or without an issue, and no open pull request | *In progress*, with the branch once it is named |
  | Open pull request | *In progress*: a row for each open, unpaused issue it closes, or one row when there are none; such an issue has no other row |
  | Closed issue; merged or closed pull request | No row, and no note of what landed, which git and the closed issues already keep |

  A row with no issue reads `owed (not filed)` in its `#` cell, the form
  `dispatch_board.py` parses beside a number. Outside `/dispatch` an
  issue's *In progress* row moves only through `dispatch_board.py tasks
  start <n>`, `state`, `done` and `back`, which take no run: `/branch`,
  `/pr` and `/ship` call them, and each writes nothing `board` would refuse
  (`docs/commands/branch.md`, *Step 5*). Work that ends with its issue
  still open (a claim abandoned, a pull request closed unmerged, or one merged
  without closing its issue) leaves its row to the table: *In progress* while
  a branch remains, which holds its paths and which `/dispatch` names to the
  owner, otherwise back to its wave. What the work found and filed has its
  own row by the same table, written when it is filed. `/dispatch` is the
  list's one writer from its step 0 to its report, and sends a hand-back or a
  stop back to its wave marked `OWNER` (`docs/commands/dispatch.md`, *One
  writer of the task list* and *Waking*). When in doubt, rebuild the list from
  `gh pr list --limit 1000` and `gh issue list --limit 1000`, because the
  default of 30 truncates silently; another session's *In progress* row stays.

  *Remaining* and *Paused* put undated rows first, then dated ones nearest
  first, and a row never sits above one it depends on. A linked worktree on
  an issue's branch means another session is working it, unless its pull
  request merged or `/dispatch` sent it back: its row is *In progress*. A tag
  such as `#G1-826` names group `G1` and issue 826,
  `#B1-929+930` two issues worked as one change; read the group's rows and
  each issue, never grep for the tag.

## Managed files

- **A managed file is changed in `blueprint-harness`**
  (`.harness/agent-rules.md`, *Working in a target*). A rule only one
  target needs goes in its `.harness/config.json`, whose pull request
  carries the re-rendered `.claude/settings.json` and manifest.
- **A payload edit can trip a target-local test the harness never runs**,
  such as a ceiling a target holds on a command's bytes: measure a grown
  file against it before the harness pull request.
- **A sync pull request is merged from its own branch**:
  `gh-pr-merge.sh` binds to the branch checked out, so `/ship sync #<n>`
  merges from a worktree on it (`/ship`, *A sync pull request is reviewed
  once, for this target*) and removes it afterwards.
  The target merges by rebase, so a merged tip is checked against the
  pull request's `headRefOid`, never with `merge-base --is-ancestor`.
