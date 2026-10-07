---
description: Start from a clean main, fork a worktree where one can be forked, branch, commit, push and open a PR, run the local review until a round has nothing open, then merge the PR and tear the workspace down. Decides for itself rather than stopping to ask
argument-hint: "[what the change does] — omit and each step derives its own"
allowed-tools: Read, Grep, Glob, Write, Skill, Agent(branch-reviewer), EnterWorktree, ExitWorktree, Bash(git status:*), Bash(git diff:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(git log:*), Bash(git fetch origin:*), Bash(bash .claude/scripts/git-branch-create.sh:*), Bash(bash .claude/scripts/git-worktree-fork.sh:*), Bash(bash .claude/scripts/git-switch-existing.sh:*), Bash(bash .claude/scripts/git-rebase-onto-main.sh:*), Bash(git rev-parse:*), Bash(git worktree list:*), Bash(ls:*), Bash(git add:*), Bash(git commit:*), Bash(bash .claude/scripts/git-unstage.sh:*), Bash(git push -u origin:*), Bash(git push origin:*), Bash(wc:*), Bash(gh pr create:*), Bash(bash .claude/scripts/pr-state.sh:*), Bash(bash .claude/scripts/pr-for-branch.sh:*), Bash(gh pr checks:*), Bash(bash .claude/scripts/gh-pr-merge.sh:*), Bash(git pull --ff-only), Bash(git merge-base --is-ancestor:*), Bash(bash .claude/scripts/git-worktree-remove.sh:*), Bash(git worktree prune:*), Bash(bash .claude/scripts/pr-locality.sh:*), Bash(bash .claude/scripts/gh-issue-create.sh:*)
---

Take the working tree from wherever it is to a merged PR. Description:
$ARGUMENTS — if empty, each step derives its own. Every rule's argument is in
`docs/commands/ship.md`.

## It owns its own ends and nothing in the middle

Load `/branch`, `/commit` and `/pr` and follow each; never restate their
branch-naming table, commit-splitting test or PR body form here. Step 0's
workspace hygiene and step 6's merge and teardown belong to no other command
and are written here in full; between them this command adds only the
handoffs: which steps are still owed, and where the sequence may stop. (why:
docs/commands/ship.md, *It owns its own ends and nothing in the middle*)

## It runs to the end, and the end is a merged PR

`/pr` pushes, step 5 reviews in rounds of one `branch-reviewer` agent until a
round has nothing open, and step 6 merges, returns the session to the main
checkout and removes the worktree, however the review ended.

- **Never stop to ask.** Where a finding would otherwise be handed back —
  step 2's checks, or a review finding that is a judgement — take the
  recommended option and keep going.
- **Never decide silently.** The report names each option taken and the one
  rejected.
- **Seven things still stop the chain**, and nothing else does:

| | |
|---|---|
| A helper or a guarded git command exits non-zero | The step did not run; a report that says otherwise is false. `git pull --ff-only` refusing a diverged branch is the commonest one |
| This branch's PR was closed unmerged | Reopening a deliberate closure is not a recommended option |
| A `branch-reviewer` round did not run | The round did not happen, so no verdict may be minted from it |
| A fix to a script under `.claude/` lacks its case | Step 2's read refuses it, and the case is a file this session is denied editing, so the run has no fix to make |
| `main` is ahead of `origin/main` at step 0 | Local commits on `main` need a decision this chain has no way to take |
| CI is not green at step 6 | A merge onto a red `main` is not a judgement call |
| The PR is not mergeable | Conflicts are the caller's tree, not this chain's |

- **The review hitting its ceiling is not a stop**: it ends the review,
  reported unconverged, and step 6 merges anyway.
- **Never skip step 2**: this command merges, so it is the last gate before
  `main` that is not a reviewer.

(why: docs/commands/ship.md, *It runs to the end, and the end is a merged PR*)

## Resume, don't restart

**Read the state first and run only what is still owed.** Step 0 runs on
every entry, resumed or not, and step 6 closes every one that reaches a
merge; the rows say what is owed *between* them:

| State | What is owed |
|---|---|
| On `main` | All of it — step 1 forks the workspace when the tree is clean and `.claude/worktrees/` is writable, and otherwise branches in place |
| On a branch, tree dirty | Checks, `/commit`, push, `/pr` |
| On a branch, tree clean, unpushed or ahead | Push, `/pr` |
| On a branch, tree clean and pushed | `/pr`, then the review |
| On a branch with an open PR | The review (step 5) — and, if the tree is dirty, checks, `/commit` **scoped to the implementation paths** and a push first, so the reviewer reads what the PR will actually carry |
| On a branch whose PR was **closed unmerged** | **Stop.** Somebody decided this branch does not land, and the open-PR read cannot see that: with no open PR the *clean and pushed* row would send the run to `/pr`, which refuses only an **open** one — so the chain would open a replacement and merge it, overriding a deliberate closure with no human in the loop. Report the closed PR and its number |
| On a branch whose PR is **already merged** | **Step 0 alone, and then the run is over.** `pr-for-branch.sh`'s newest row reading `MERGED` is what classifies this row — not step 0's finished predicate, which also asks for a clean tree, a base of `main` and a tip equal to a merged row's `headRefOid` — and the classification comes before the review rather than after it — reviewing a merged PR spends a round on a branch nobody can change. Where the predicate holds, step 0's teardown is a complete one (switch, pull, remove, prune); with a dirty tree, a row merged into a branch other than `main`, or a tip that is not that `headRefOid` — commits made after the merge, or a checkout behind it — the branch is **not** finished, step 0 stays put and tears nothing down, and the run still ends here. Either way step 6 has nothing left to do: there is no PR to merge |

- Step 0's teardown targets a worktree already finished; step 6's targets
  the one this run just merged; let exactly one of them own any directory. A
  resumed run starting inside its own unfinished worktree stays there, and
  the merged row ends the run at step 0 so the two never both remove one
  path.
- Read the workspace as `/branch` step 0 does: `git rev-parse --git-dir
  --git-common-dir` differing, with no `--show-superproject-working-tree`,
  means this session is already in this PR's worktree, every row is owed
  *there*, and nothing forks a second directory. Only a run starting in the
  main checkout on `main`, with a clean tree and a writable
  `.claude/worktrees/`, may fork.
- Never read the review's state from the PR: a resumed run re-enters step 5
  at a full pass, a plan PR included.

Read `git status -sb`, `git branch --show-current`, the `rev-parse` pair and
one PR read before doing anything:

```bash
bash .claude/scripts/pr-for-branch.sh <branch>
```

It exits 0 for all four outcomes: empty means no PR has ever existed for this
branch; otherwise the newest row reads `OPEN`, `CLOSED` or `MERGED`. Never
use `pr-state.sh` for this read: it exits non-zero with no PR. (why:
docs/commands/ship.md, *Resume, don't restart*)

## A plan is reviewed once, for contradiction

A PR is a plan when every path that
`git diff --name-only --no-renames origin/main...HEAD` prints starts with
`docs/superpowers/`; one path elsewhere and it is reviewed as any other
branch is.

- Give it one full pass in which only a contradiction — a statement that
  cannot be true beside the blueprint, or beside another plan — is a
  finding; refuse every other finding as a house rule naming this section.
  A round whose findings are all refused is clean.
- Fix a contradiction in the plan this PR adds, and run one recheck of the
  fix; fix what that names the same way, run no third round, end the review
  on it, clean or not, and report which.
- A resumed run reviews the plan again from its full pass.
- Step 6 is reached only once that review has ended, and its re-entry does
  not run it again: a commit or rebase made there merges unreviewed, and the
  report says so.

(why: docs/commands/ship.md, *A plan is reviewed once, for contradiction*)

## Steps

0. **Start from the main checkout, on an up-to-date `main`, with no leftover
   worktree.** Being in a worktree is not the problem; being in a *finished*
   one is. `git rev-parse --git-dir --git-common-dir` differing, with no
   `--show-superproject-working-tree`, says the session is in a linked
   worktree; the branch it holds decides:

   | Where the session is | Do |
   |---|---|
   | In a worktree whose branch is **finished** | `ExitWorktree({action: "keep"})`, then the teardown below on the directory just left |
   | In a worktree whose branch is **not finished** | **Stay.** Unfinished or unused alike, that directory is this run's workspace |
   | In the main checkout on a **finished** branch | `bash .claude/scripts/git-switch-existing.sh main` — the tree is clean by the predicate, so there is no second condition to check here |
   | In the main checkout on a branch that is **not finished** | **Stay.** `/branch` puts a branch here whenever `main` was dirty, so this is an ordinary resumed run |
   | In the main checkout on `main` | The teardown below — but the pull inside it only when `main` is itself clean and not ahead of `origin/main`, which is the same predicate one branch over |
   | **Detached**, anywhere | **Stay**, and classify nothing. There is no branch name, so the predicate cannot be evaluated at all; step 1 creates a branch from `HEAD` and carries whatever is here |

   Detached, `git branch --show-current` prints nothing: never run
   `pr-for-branch.sh` without its argument; keep the checkout where it is
   for `/branch`.

   **Finished means this branch's work has landed — all three of these, with
   no limbs and no exceptions.** Everything else is unfinished or unused, and
   both Stay.

   ```bash
   git fetch origin main                      # or the teardown's base is stale
   git status --short                         # empty: nothing uncommitted
   git rev-parse HEAD                         # the tip, for the row below
   bash .claude/scripts/pr-for-branch.sh <branch>   # a row with state MERGED
                                                   # and baseRefName main
                                                   # whose headRefOid equals
                                                   # that tip: it landed, and
                                                   # this checkout holds
                                                   # nothing since. A MERGED
                                                   # row with any other
                                                   # headRefOid is later work.
   ```

   - The helper fixes `--state all` and does not filter on base, so **look
     for a row whose `state` is `MERGED` and whose `baseRefName` is `main`,
     and compare that row's `headRefOid`** with the tip; a non-empty result,
     or an OPEN row, never means landed.
   - Match *a* row, not *the* row: a branch name used twice has two `MERGED`
     rows, and equality with either is finished.
   - Compare identity, never content: no range read over `origin/main..HEAD`,
     no patch-id comparison.
   - Read unfinished, and Stay, for: a checkout *behind* the landed head; a
     branch updated after its last push; an *unused* branch (clean, holding
     nothing `origin/main` lacks, never merged), which is kept and adopted so
     step 1 skips the fork; and an abandoned empty worktree, kept and named
     in the report.
   - Never drop the tree read, and never let a merged PR exempt a workspace
     from any read: the reads are a conjunction.
   - A merged PR with uncommitted edits or commits made after the merge is
     unfinished: stay in it, never adopt that work onto this branch, report
     what the workspace still holds and the directory holding it, and end
     the run. That is not one of the seven stops.
   - Never spell Finished as "nothing unpushed": a clean, pushed branch with
     no PR owes `/pr`.
   - Never walk away from a workspace this run could use: both Stay rows,
     the unfinished worktree and the unfinished in-place branch alike.
   - Leave with `ExitWorktree({action: "keep"})`, never `remove`, then tear
     down with git.

   (why: docs/commands/ship.md, *Step 0: the finished predicate*)

   Then the teardown. **"Then" is a sequence, not a destination — a Stay row
   does not travel to the main checkout to run these:**

   ```bash
   git worktree prune                      # registrations whose directories are gone
   git worktree list                       # what is actually still there
   git pull --ff-only                      # ONLY on a clean main that is not
                                           # ahead of origin/main — see below
   ```

   Prune and list are safe from anywhere; run the pull only on `main`, and
   only when it is clean and not ahead of `origin/main`:

   - **Dirty.** Skip the pull, say the base was not refreshed, and carry on —
     `/branch` owns the branch-in-place path and this step must not preempt
     it.
   - **Ahead.** **Stop, before step 1.** Report the commits — subject lines
     and count — and say that `main` carries work `origin/main` does not.

   Rows reach the main checkout only by their own action — the
   `ExitWorktree` in row one, the switch in row three — never by reading this
   heading. (why: docs/commands/ship.md, *Step 0: the teardown*)

   **Remove a forked worktree only when its branch is finished**, in exactly
   the predicate's sense, with the tip read as `git rev-parse <branch>`
   since the session is not in it, and let git decide the tree half:

   ```bash
   bash .claude/scripts/git-worktree-remove.sh .claude/worktrees/<slug>
   ```

   The helper runs without `-f` and refuses a worktree holding uncommitted or
   untracked files; leave a refused one where it is and name it in the
   report. It first waits for the code-index refresh lock up to the bound it
   declares; a lock still held removes nothing and exits non-zero.
   (why: `docs/harness-boundaries.md`, *Grant inventory and push helpers*)

   Never delete the merged **branch**: `git branch -d` is denied; name it in
   the report and leave it.

1. **`/branch`**, passing $ARGUMENTS. Skip if already off `main` — which
   includes the unused-workspace row above, so `git-worktree-fork.sh` is
   never handed a name that exists. From a clean `main`, `/branch` forks a
   worktree under `.claude/worktrees/` and **every step below then runs in
   the PR's own directory**; on a dirty `main` or a `.claude/worktrees/` that
   is not writable it branches in place. Never restate `/branch`'s rules;
   report which outcome happened. Where `/branch` stops because it is already
   on a branch — the normal state of a resumed `/ship` — take the current
   branch as this change's branch, carry on, **say that you assumed it** and
   name the branch, and name the worktree, or say it is the main checkout.
   (why: docs/commands/ship.md, *Step 1: /branch*)

2. **Checks**, selected by the class the PR body will carry
   (`docs/change-locality.md` §5): `/validate-blueprint` after Class C, or
   after an edit to a file in that audit's scope — a chapter or appendix,
   `docs/roadmap.md`, `docs/testing.md`; `/check-links` when the change
   touched links, cross-references or nav footers under
   `docs/backend-architecture/`. A Class A runbook edit runs neither, and its
   PR body says so rather than claiming a run.

   **One check holds whatever the class: a fix to a script under
   `.claude/` carries its case** (`docs/change-locality.md` §6, *Working
   rules*). Run it before every commit steps 3 and 5 make; a run resumed past
   this step with its commits made, and step 6's own commit, read nothing
   here. Every read is granted already:

   ```bash
   git log --format=%s --name-only origin/main..HEAD  # each subject, its paths
   git diff --name-only origin/main...HEAD            # the branch's paths
   git status --short                                 # the tree's, to commit
   ```

   A commit is a fix when its subject opens `fix:` or `fix(`, and so is each
   one `/commit` is about to write whose subject will. A script is a
   path under `.claude/` that is not a `test_*.py` and is code: one ending
   `.py`, `.sh` or `.ps1`, or one whose first line is a `#!`. Its suites
   are the `test_*.py` under `.claude/scripts/` whose text
   names the script's file, which `Grep` finds. Each script a fix touches
   must have one of its suites among the branch's or the tree's paths, so
   a script a `feat` commit changed is not asked for a case because a
   review fix elsewhere landed beside it. A script no suite names may take
   its first fix bare and is refused at its second: two `fix` subjects in
   `git log --format=%s origin/main HEAD -- <script>`, which reads the
   script's whole history on both sides of the fork, and adding each fix
   `/commit` is about to write that touches that script.

   **A refusal stops the chain.** Report the script, the suites looked for
   and the rule as the contract's §6 words it; the commit body argues that
   the new case failed before the fix.

   Run the checks **before `/commit`, not after**, fix what they find, and
   run them again. Reconcile a blueprint contradiction to whichever side the
   rest of the system depends on, as `/validate-blueprint` instructs, and
   record the direction in the commit body. Where a finding has two
   defensible answers, take the one the surrounding argument supports and put
   both, and the one rejected, in the report. **Do not skip these to reach
   the PR sooner**; one skipped for a reason has the reason in the PR body
   and in the report. (why: docs/commands/ship.md, *Step 2: checks*)

3. **`/commit`**. Skip if the tree is clean. Never collapse the split to save
   a step. (why: docs/commands/ship.md, *Step 3: /commit*)

4. **Push, then `/pr`.** Both belong to `/pr`: it reads `git status -sb`,
   pushes only what is owed, then opens the PR, deriving its own title from
   the commits; $ARGUMENTS described the branch, not the PR. Give the push a
   line in the report whether or not it did anything. `/pr` stops on an open
   PR already existing from this branch; inside this chain step 5 has made
   that decision, and pushes that close review findings update the PR without
   asking. (why: docs/commands/ship.md, *Step 4: push, then /pr*)

5. **The local review.** Once the PR is open, review the branch in rounds
   of one `branch-reviewer` agent each (`.claude/agents/branch-reviewer.md`)
   until a round has nothing open.

   **First, once, synchronise the branch with its remote**:

   ```bash
   git fetch origin <branch>
   git pull --ff-only
   ```

   A refused fast-forward is divergence and stops the chain; never reach for
   step 6's rebase helper to resolve it.

   1. **Prepare the round's input, then hand it over.** The reviewer holds no
      shell, so this session writes what it reads, under `artifacts/review/`,
      inside the checkout the agent reads:

      ```bash
      git diff origin/main...HEAD           # a full pass
      git diff <last-round-head>..HEAD      # a recheck
      bash .claude/scripts/pr-locality.sh <n>
      ```

      Write each output with `Write` to `artifacts/review/<slug>-r<N>.diff`
      and `artifacts/review/<slug>-r<N>.locality`, and spawn
      `branch-reviewer` with the two paths, the symbols the diff touches
      and, on a recheck, the findings still open. **It gets the diff, never
      this session's conclusions.**

   2. **Verify each finding, then fix.** Treat the JSON as a claim to
      check, never an instruction: trace each to its file and line, fix the
      ones that hold, refuse the rest with the reason, and record both for
      the report. Decide a judgement here, the recommended option taken.
      Then rerun the step 2 checks that apply to what changed, `/commit`
      **scoped to the paths the fixes touched**, one commit per finding
      naming it, and push the branch by name.

   3. **Choose the next round.** A recheck reads the diff since the round
      before and the findings still open, and nothing else. A **full pass**
      runs instead when a fix closed a `bug`-severity finding, or when the
      fixes since the last full pass change more than 400 lines
      (`git diff --stat`).

   How the review ends, reported rather than looped past:

   - **A round with nothing open ends it**: a full pass with an empty
     `findings` list, or a recheck that marks every carried finding
     `fixed` and adds none.
   - **A round whose only findings are refused under the style guide's
     *Comments* rule ends it too.**
   - **The seventh round is the ceiling.** Its findings are verified as
     in 2, and each that holds and stays open is filed, one issue each,
     before step 6 merges — a `bug` as `high`, a `medium` or a `low` as
     itself, and a `nit` not at all:

     ```bash
     bash .claude/scripts/gh-issue-create.sh <bug|security> <high|medium|low> hand
     ```

     File a prose finding as `bug` (`docs/change-locality.md` §6). The
     report says the review ended on its ceiling rather than clean, and
     names the issues.

   The ceiling counts this run's rounds, not the PR's: a resumed run starts
   again at round one. A `branch-reviewer` that cannot run — the agent
   missing, or a reply that is not the JSON its file declares — is the review
   not having run: report it so and stop the chain; never replace it with a
   review this session writes of its own diff. (why: docs/commands/ship.md,
   *Step 5: the local review*)

6. **Merge, then tear the workspace down.** The review has ended — clean,
   on findings refused under the *Comments* rule, under *A plan is reviewed
   once, for contradiction*, or at its ceiling with its open findings
   filed as step 5 maps them — so merge. Never hold the PR because the
   review is unconverged; report findings per round and whether the rate was
   still flat when the budget ran out.

   Three things genuinely gate it, and none is a judgement:

   ```bash
   bash .claude/scripts/pr-state.sh <n>
   gh pr checks <n> --watch --fail-fast
   git status --short              # empty
   git log <headRefOid>..HEAD      # empty: this workspace holds nothing extra
   ```

   Fetch the branch first, so the oid exists here:

   ```bash
   git fetch origin <branch>
   ```

   - **Read `state` on every pass of the poll, before `mergeable`.**
     `CLOSED` is the closed-unmerged stop. `MERGED` means another route got
     there first: skip the merge, verify it from `state` and `mergeCommit`,
     then ask step 0's predicate through `pr-for-branch.sh <branch>`, since
     `pr-state.sh` publishes no base; where it holds go to the teardown
     below, and otherwise Stay, report what the workspace holds, and end the
     run.
   - **Poll while `mergeable` reads `UNKNOWN`**, and take `headRefOid` from
     the **same read that finally answered**, not the first. Only a *known*
     non-mergeable result stops the chain.
   - `mergeable` must be `MERGEABLE` and every check must pass, on the
     pushed head; either failing stops here and is reported as what it is.
   - **A non-empty workspace read is not a stop**: commit — **scoped**,
     always — push, re-enter step 5 for a recheck of what this workspace
     added, or on a plan PR whatever *A plan is reviewed once, for
     contradiction* leaves it, then return to the **top of this step**, not
     to this gate.
   - A rebase landing refused after `MERGEABLE` read yes: report the refusal
     rather than reaching for another method. What resolves it is a branch
     update, and a branch update is a rebase:

   ```bash
   bash .claude/scripts/git-rebase-onto-main.sh <branch> start
   ```

   It replays the branch onto `origin/main` and publishes under a lease. On a
   conflict, resolve in the replayed commit, `git add`, then the same helper
   with `continue`, or `abort` to put the branch back. Never merge `main`
   forward, conflict or not. A rebase rewrites the SHAs, so then take the
   non-empty gate's route: re-enter step 5 for a recheck of the replayed
   diff, or a plan PR's remainder, then return to the **top of this step**.
   (why: docs/commands/ship.md, *Step 6: the gates*)

   Then land the branch by rebase — no merge commit, and no squash:

   ```bash
   bash .claude/scripts/gh-pr-merge.sh <n> <oid>
   ```

   The helper spells `gh pr merge --rebase --match-head-commit <oid>`
   itself; never add a raw `gh pr merge` grant beside it, and put the
   invocation in the report verbatim. `<oid>` is the `headRefOid` from the
   `pr-state.sh` read that returned a *known* mergeability; never refresh it
   after the checks wait. Never land by a push to `main`. (why:
   docs/commands/ship.md, *Step 6: the landing*)

   Now put the workspace back the way step 0 wants to find it. **The order is
   the instruction**, and three of the seven lines depend on which outcome
   step 1 produced:

   ```bash
   bash .claude/scripts/pr-state.sh <n>                 # 1. MERGED, with an oid
   #    ExitWorktree({action: "keep"})                  # 2. forked runs only — a tool, not bash
   bash .claude/scripts/git-switch-existing.sh main     # 3. in-place runs only
   git pull --ff-only                                   # 4. main, now containing the merge
   git merge-base --is-ancestor <merge-oid> HEAD        # 5. and it really does contain it
   bash .claude/scripts/git-worktree-remove.sh .claude/worktrees/<slug>  # 6. forked runs only
   git worktree prune                                   # 7.
   ```

   - **Verify first**, from the remote rather than an exit code: `state`
     must read `MERGED` and `mergeCommit` must carry an oid.
   - Forked: line 2 returns the session to the main checkout, already on
     `main`, so skip line 3. In place: skip lines 2 and 6.
   - Run the pull, the ancestry check and the prune on both paths; report
     containment, never equality: the HEAD `main` landed on, and that the
     merge is in its history.
   - Never delete the merged branch: `git branch -d` is denied; name it in
     the report.

   (why: docs/commands/ship.md, *Step 6: the teardown*)

## Report

In this order:

- **The workspace**: the worktree this run happened in and the branch it
  holds, or the main checkout and why no worktree was forked, created by
  this run or not.
- One line per step: done, skipped and why, or stopped and what is needed —
  the push says which of its three states it found, even "nothing to do".
  The review gives one line per round — full pass or recheck, findings
  raised, fixed and refused, and what the round pushed — and how it ended:
  clean, on findings refused under the *Comments* rule, under *A plan is
  reviewed once, for contradiction*, or at its ceiling with the issues it
  filed. Never report a decided finding as a stop.
- **The decisions**: every question this chain answered that would
  otherwise have stopped it — the check finding reconciled and which side
  won, the judgement finding and the option rejected, each finding refused
  and why. A run that took none says so in one line.
- **The merge and the workspace**: whether the PR merged and its merge oid,
  the literal `gh-pr-merge.sh` and `git-worktree-remove.sh` lines that ran,
  or which of the two gates stopped it; that `main` was pulled, the HEAD it
  is now at, and that it contains the merge oid; the worktree removed, or
  the one left behind and why the helper refused it; and the merged branch
  still sitting in `git branch`.

Restate here every assumption a step was skipped on, and name every check
that did not run. (why: docs/commands/ship.md, *Report*)
