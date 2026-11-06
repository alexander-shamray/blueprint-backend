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
  reports it conflicting or refuses its rebase landing, and only a branch
  this session owns. `git-rebase-onto-main.sh <branch> start` is the
  update: it fetches `origin/main`, replays the branch onto it and
  publishes under a lease. Set `git config rerere.enabled true` once per
  clone. `/ship` step 6 merges after its own wait on the checks.
- **A pull request that is ready merges in the turn that establishes it
  is ready**, a Dependabot one aside, which the owner merges: every check
  settled green, the review ended and no blocking deny left open.
  Handing a ready pull request back with merge instructions makes the
  owner a queue step for a decision already made.
- **A Dependabot pull request is the one exception to the trailer and to
  the class row.** It declares no class and no touch set and carries no
  `PR:` trailer, so the gates that read them skip it, and a skipped job
  passes a required check. Every other job runs on it, and the owner
  merges it. The exemption is by author, so a human push to a Dependabot
  branch rides on it; review such a push as a gate bypass. With no
  trailer, `git-revert-pr.sh` cannot find its commit, so one is reverted
  with `git revert` on a branch of its own.
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
  at the merge.
- **A red `main` is reverted first and fixed forward second.** The next
  pull request to land reverts the one that turned `main` red, by
  `git-revert-pr.sh <n>`; the fix forward is a pull request of its own
  after it. A red run that a rerun turns green is a flake: it is a comment
  on the flaky tests tracker, and an issue filed for the red `main`
  closes on that green run.

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
  `gh-issue-create.sh` takes the area as its third argument; a label
  outside its set, `tracking`, `paused` or `flaky`, is added by
  `gh issue edit` after filing. A severity in the body and the label both
  say it. `documentation` is an issue's kind only on one that carries no
  other.
- **An issue's comments are read before it is judged unblocked**: the
  owner records live measurements that block an issue as comments.
- **The task list** is where `.harness/agent-rules.md`, *Working in a
  target*, puts it. Its *Remaining* and *Paused* sections put undated rows
  first, then dated ones nearest first, and a row never sits above one it
  depends on. A linked worktree on an issue's branch means another session
  is working it: its row is *In progress*. A tag such as `#G1-826` names
  group `G1` and issue 826, `#B1-929+930` two issues worked as one change;
  read the group's rows and each issue, never grep for the tag.

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
