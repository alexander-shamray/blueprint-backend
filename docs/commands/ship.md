# /ship — the argument

This file holds why `/ship` is shaped the way it is; the runbook is
`.claude/commands/ship.md`, and it wins where they differ. Read this
when the command is disputed or edited, not when it runs.

## It owns its own ends and nothing in the middle

`/branch`, `/commit` and `/pr` hold the branch-naming table, the
commit-splitting test and the PR body form. **Load each and follow it. Do not
restate them here** — a chainer that paraphrases the steps it calls is the
worst place for a second copy of a rule to live, because it is the copy that
drifts.

**The two ends are different, and the runbook is the only place they are
written.** Step 0's workspace hygiene and step 6's merge and teardown belong to
no other command — there is nothing to delegate to and nothing to restate — so
the runbook writes them in full and this file argues them. Between them, this
command adds only the handoffs: which steps are still owed, and where the
sequence is allowed to stop.

## It runs to the end, and the end is a merged PR

`/pr` pushes the branch itself, so the chain reaches an open PR without waiting
for anyone, and step 6 merges it. Step 5 sits between: a `branch-reviewer`
agent reads the branch, this session verifies and fixes what it found, and
the rounds go on until one has nothing open. When the review has ended —
however it ended — the PR is merged, the session returns to the main
checkout and the worktree is removed.

**Nothing in this chain stops to ask.** Where a finding would otherwise be
handed back — step 2's checks, or a review finding that is a judgement — the
run takes the recommended option itself and keeps going. That is the
caller's standing instruction and not a judgement about the findings.

**Deciding is not the same as going quiet.** A decision taken here is written
down where the person who would have been asked can find it: the report
names the option taken and the one rejected. A silent decision is the
failure mode this rule creates; a stated one is the thing it trades an
interruption for.

Seven things still stop the chain, and the runbook's stop table names them.
Its helper, reviewer and missing-case rows are questions about *this* run; the
other four are questions about the repository's state, and no recommended
option exists for any of them. Two of the four are
somebody's decision this chain would otherwise undo in silence — commits
placed on `main`, and a PR deliberately closed — which is a sharper reason
to stop than not knowing what to do.

**The review hitting its ceiling is not in the runbook's stop table.** A
ceiling ends the review — it reports itself unconverged and step 6 merges
anyway, because a budget running out is not a verdict. Reading it as a
chain stop would hold every PR whose reviewer had more to say, which is the
opposite of what step 6 decides.

**The checks carry the weight a stop would.** This command merges, so step 2
is the only thing between a bad edit and `main` that is not a reviewer, and
**skipping it is not a minute saved — it is the last gate**.

## Resume, don't restart

**Step 0's teardown targets a worktree that is already finished; step 6's
targets the one this run just merged. Exactly one of them owns any given
directory.** A resumed run that starts inside its own unfinished worktree stays
there — the runbook's step 0 table says so in its second row, and that row is
what keeps this step from stranding the branch it was meant to tidy up around.

**The merged row is the case where the two could collide, which is why it ends
the run at step 0.** A session standing in a worktree whose PR is already
merged is finished by step 0's first row *and* would be "this run's" by step
6's. Both tearing it down means the second `git-worktree-remove.sh` runs
against a path that is no longer a worktree, exits non-zero, and stops the
chain on a helper failure with no defect behind it. So that row is step 0 and
nothing after: there is no merge left to perform, and the teardown has
already happened.

**The workspace is part of that state**, and it is read the way `/branch`
step 0 reads it: `git rev-parse --git-dir --git-common-dir` differing, with no
`--show-superproject-working-tree` to make it a submodule, means this session
is already inside this PR's worktree. Then every row of the runbook's resume
table is owed *there* and
nothing forks a second directory. A run that starts in the main checkout on
`main` is the only one that can fork a workspace at all — and only with a clean
tree and a writable `.claude/worktrees/`, per step 1's two exceptions.

**The review's state is not read from the PR, so a resumed run re-enters
step 5 at a full pass.** Its rounds leave findings in one run's context and
diffs under `artifacts/review/`, neither of which a later session can trust
as a verdict on the current head; a full pass over a clean branch finds
nothing and costs one round, and a re-run is proof where an inference would
be a guess. A plan PR is no exception, and *A plan is reviewed once, for
contradiction* says what its re-run costs.

**One call to `pr-for-branch.sh`, four outcomes, and it exits 0 for every one
of them.** Empty means no PR has ever existed for this branch; otherwise the
newest row reads `OPEN`, `CLOSED` or `MERGED`, and those are precisely the
four cases the runbook's resume table distinguishes.

**`pr-state.sh` cannot be that read, and the reason is an exit
code rather than a preference.** With no PR for the current branch it exits
non-zero, and *forked but never PR'd* is what step 1 produces on every run —
so the commonest state in the resume table would be classified through a failed
command, in a chain whose first stop rule is that a non-zero exit means the
step did not run.

## A plan is reviewed once, for contradiction

**A pull request whose diff lies wholly under `docs/superpowers/` is a plan,
and it gets one review round in which only a contradiction is a finding.**
It is one when every path that
`git diff --name-only --no-renames origin/main...HEAD` prints starts with
that prefix; one path elsewhere and it is reviewed as any other branch is.
`--no-renames` is what makes a move count: without it a file moved into the
prefix prints only its new path, and the path it left is never tested. A
plan is a pre-build record from the moment it merges and is never edited
after, so wording, completeness and prose are not findings in it. A
statement that cannot be true beside the product specification
`docs/harness-local.md` *Documents* names, or beside another plan,
is a finding, and its fix lands in the plan this PR adds, because the
others are already frozen.

**One full pass, and a recheck only when it names a contradiction.**
Every finding but a contradiction is refused as a house rule naming this
section, and a round whose findings are all refused is clean. A
contradiction is fixed in the plan this PR adds, and one recheck reads the
fix; what that names is fixed the same way and no third round runs, so the
review ends on it, clean or not, and the report says which. A resumed run
reviews the plan again from its full pass: the round is cheap, and no record
of an earlier one is on the PR.

**Step 6 is reached only once that review has ended, and its re-entry does
not run it again.** A commit or rebase made there merges without a review
reading it, and the report says so, because a plan is one round and step 6
cannot buy it a second.

## A sync pull request is reviewed once, for this target

**A branch name proves nothing, so a sync is what `sync_pr.py verify` says it
is.** Any same-repository branch can be named `chore/harness-sync-xxxxxxx`;
`verify` is what shows the head holds the payload at a commit on the harness's
`main`, holds the settings it renders, and changes nothing the config keeps
(`docs/harness-boundaries.md`, *Grant inventory and push helpers*). It runs in
the main checkout before a worktree exists, and a head that fails it is neither
checked out nor reviewed, because the review that follows would be of a diff
nobody proved. That is not
one of the seven stops, which are things that failed while the chain ran and
here nothing ran, but for two cases: a PR closed or merged under the run fails
the verify as not a sync, so `pr-state.sh <n>` is read. `CLOSED` is the
closed-unmerged stop, ending `stopped` with reason `closed unmerged`, which a
later superseding sync depends on, and `MERGED` takes step 6's route for a merge
another route made, since reporting it unmerged would be untrue. A PR closed
before `list` has no head to read, which is why the state is read by number. A
session reports the verify's lines and ends unmerged; an executor hands the
change back with them as the reason, since nobody is there to read a report.

**The worktree is opened on the branch that exists, not forked from `main`.**
`/branch` would name a new branch, and a sync's commits are the harness's: the
PR's head is the branch `gh-pr-merge.sh` binds to, so the merge must run from a
checkout of it. `git-worktree-sync.sh` fetches the branch and adds the worktree
tracking `origin/<branch>`, refusing any name that is not a sync head and every
path and root `git-worktree-fork.sh` refuses. A sync taken again after the fix
it waited for has merged finds that worktree and branch where the unmerged
ending left them, and the helper would refuse a second make, so the run enters
the one that exists and makes the same comparisons.

**Step 2 runs the target's own suites and nothing else.** The managed files were
checked in the harness, so *Ship checks* would read them a second time; what the
harness never runs is the target-local test a payload change can trip
(`docs/harness/working-rules.md`). Each is run as CI runs it, with `discover -s
<dir> -p <file>`, because it imports its siblings from its own directory and a
dotted module name cannot be formed from `.claude/` or `.github/comment-gate/`.
The command is granted in the frontmatter, since a dispatched run's reads must
be named by its own grants. A failing suite has no ending of its own, since step
2 otherwise says to fix what the checks find and a sync branch is never
committed to: it is a target-local finding, filed in the target, and the sync
ends unmerged or is handed back with no round run, because the code under test
is the target's and a review of the managed files cannot clear it. A re-take
keeps `HEAD` at `<oid>` and so reruns the head's old copy of the suite: the fix
the finding waits for is on `main`, not in this head, so only a later sync cut
from the fixed `main` clears it. A finding in a local file the round reads is
the other kind: the re-take's review diff reads main's tree, so the merged fix
clears it.

**Steps 3 and 4 do nothing, because a sync branch is never committed to.** A
commit would make the head something `verify` never proved and the next sync
overwrite it. Step 6's rebase helper is the one publish, a replay the head is
re-proved after. A dirty tree is the sync row's own case, not the open-PR row's,
whose answer is to commit: it is reported, with what it holds, and the run ends
unmerged, and so it is at step 6, whose own route for a non-empty workspace
commits, pushes and re-enters step 5.

**One round, because the diff is the part that is this target's.** The reviewer
is handed `review-diff`'s file, which leaves the managed content out, and not
the branch diff, which holds every managed file once more. The file is written
by the helper and never with `Write`: the helper proves the head first, and a
file this session composed would not be that proof. Step 5's opening synchronise
is skipped, because the branch tracks `origin` and the pull fast-forwards: a
push after the proof would move `HEAD` and `review-diff` would then run the
unproved head's script. A finding is filed in the repository whose code it is
about, citing the sync, with the severity map the reviewer's words need for the
filing helper, which takes `critical|high|medium|low`: a `bug` as `high`, a
`medium` as itself. The sync then ends unmerged or is handed back. A payload
finding is the harness's, so the fix's own sync supersedes this one. A
target-local one, such as a local file citing a path the sync deletes, is fixed
in the target, and this sync is taken again once that merges. The harness is not
the checkout's repository, so a payload finding is filed with
`gh-issue-harness.sh`, whose repository is a literal and which only `/ship`
holds: a route or repository word to `gh-issue-create.sh` would be declared by
the caller, and the sweeps hold that helper over a tree they cannot trust. A
`low` goes where its code is, as the user-level filing rule sends it: a payload
one into the one harness issue, and a target-local one into the target's own
one; the merge goes on, and a `nit` is not filed. The harness filer only creates
on a first filing: a re-taken sync meets the issue its first round filed, so it
first searches the open issues for that exact title, with every call under a
timeout, and comments on a match, and a failed search files nothing. Its area is
the finding's tree, as `docs/harness/working-rules.md` maps it: `.claude/` is
`harness`, other prose `documentation`, the rest `code`, with `test` beside
`harness` or `code` for a suite. A target-local low goes through
`gh-issue-create.sh`, which always creates, so a re-take reads
`gh-issue-list.sh` first, and where an open issue already holds that title
nothing is filed. A low that issue does not list stays in the run's report,
naming the open issue's number, and under `--dispatched` in the result's
`decisions`, since a dispatched merge's result has no other slot for it, and
since the check is by title and not by item. A re-take that cannot find the
citing line it raised gets the review diff's citations from main's tip and not
the head's, because a sync never changes a local file and the head is not
rebased, so its copy still holds a line the target's own fix removed. A
hand-back for a low would be a stop, and the rule already sends lows to an
issue.

**Step 6 verifies again after a rebase, and that replaces the recheck.** A
rebase rewrites the head, so what was proved is gone, and `verify` on the new
head is the proof again. The step's ordinary route re-enters step 5 for a
recheck after a rebase; a sync keeps its one round, because the verify compares
the new head's managed files by blob id. A failed verify is handed back, as the
first one is.

**What was proved is what runs and merges, so the proof carries the head, and
the first proof is not made by the tree it proves.** A `verify` run inside the
worktree executes that head's own `sync_pr.py`, and a forged head carries one
that always prints `verified`. So the first proof is made in the main checkout,
which must be on `main`: on another branch it verifies with that branch's copy,
so the run reports and ends unmerged. It uses the target's own `sync_pr.py` on
`<oid>` from `list`, and `--head <oid>` makes the listing and the fetch each
exactly that oid. In the worktree, `git rev-parse HEAD` must equal `<oid>` and
`git status --short` must be empty before anything there runs: the commit is not
the files, and a worktree that already existed on the right commit can hold a
modified `sync_pr.py` or suite that step 2 would run before any later dirty
read. Both together are what make the worktree's managed copy of `sync_pr.py`
the proved one, and a difference is reported and ends unmerged. Every later
`verify` and `review-diff` passes the same `--head <oid>`, because the branch
could move before step 2 runs its suites, and step 6's `<oid>` is that value,
since `pr-state.sh` reads the pull request's current `headRefOid` and a push
after the proof would otherwise land unproved. One that differs is a moved sync,
handed back with no merge. After step 6's rebase the new `HEAD` is the head, the
replay being the session's own. A run that starts inside a sync worktree holds
no proof, as nothing says what put that tree there: if it entered the worktree
this session it leaves with `ExitWorktree`, which is why the rows that keep a
run in its worktree except it, proves the head in the main checkout and enters
again. A session that did not enter it gets an error from `ExitWorktree`, so it
reports and ends unmerged, to be started again from the main checkout. A
`--dispatched` sync run starts inside its worktree too, but the dispatcher made
the proof in the main checkout, and `dispatch_board.py sync-launch` and every
relaunch's `run` refuse a tree that is not that head or is not clean, so the run
takes `<oid>` as `git rev-parse HEAD`. That bind has to be the dispatcher's
code: the run's own `sync_pr.py` is the head's copy, so a proof made here would
be the tree vouching for itself.

**Under `--dispatched` the group is `sync-<n>` and `issues` is `[]`.** The
argument carries no `#<group>-<n>` tag for the Report's rule to read, and a sync
answers no issue. `/ship` is granted `sync_pr.py` and the worktree helper in its
frontmatter as well as in the settings, and the harness filer in its frontmatter
alone: a dispatched run's reads must be named by its own grants, and one they do
not name is the auto classifier's to refuse.

## Step 0: the finished predicate

**Start from the main checkout, on an up-to-date `main`, with no leftover
worktree.** A run that begins inside the *previous* PR's directory is the
failure this exists to prevent: step 1 reads "already on a branch", adopts
it, and the whole chain commits this change onto the last one's branch.

**Being in a worktree is not by itself the problem — being in a *finished*
one is.** `git rev-parse --git-dir --git-common-dir` differing, with no
`--show-superproject-working-tree`, says the session is in a linked
worktree; what decides whether to leave it is the branch it holds, as the
runbook's table sets out.

**The detached row is not a special case of the others, it is the absence
of the thing they read.** `git branch --show-current` prints nothing, so
`pr-for-branch.sh <branch>` has no argument and the promised exit-zero
classification cannot be attempted, let alone answered — the step would
stop on a malformed command before `/branch` ever got the chance to make a
branch out of the state. `/branch` handles this deliberately (it is the
shape a sweep's worktree has), and the only thing step 0 owes it is to keep
the checkout where it is.

**The question is identity, not content: is this still the commit the
pull request landed?** `pr-for-branch.sh` publishes each row's
`headRefOid`, and the commit half of finished is `git rev-parse HEAD`
equalling the one on a row `MERGED` into `main`. Anything committed since
moves the tip, whatever its patch looks like and whether or not it is a
merge — there is no shape of post-landing work that survives this read.

**The row's `baseRefName` must be `main`, because landed means landed
there.** A stacked pull request merged into another branch has a `MERGED`
row whose head is the tip, and `main` holds none of it. The helper
publishes the base and does not filter on it, since the resume table reads
the same rows for a question that has no base in it.

**No comparison of content can stand here, and the two obvious ones fail
in opposite directions.** A range read over `origin/main..HEAD` cannot see
a rebase landing at all: step 6 lands with `--rebase`, the replay gives the
branch's commits new SHAs, so the range is never empty, no landed branch is
ever finished, and every worktree is kept — wrong the safe way, costing a
directory nobody removes. A patch-id comparison does answer for a rebase
landing and then answers the wrong question: `git cherry` reports a commit
whose patch `main` already carries as `-`, and omits merge commits
outright, so it calls a workspace finished while it holds a commit made
after the landing, and needs a second read over the range's merges to see
a resolution recorded only in one. Step 0's response to finished is to
remove the worktree, so that is wrong the unsafe way, and a predicate
patched by a second predicate is the shape to leave rather than extend.

**Identity is also method-agnostic, which is why it is the right read
rather than the safer one.** Merge, squash or rebase, the head a pull
request merged is the head it merged, so the landing method never reaches
this predicate.

**A checkout *behind* that head is kept as well, and that is the cost of
one read.** A clean HEAD that is an ancestor of the landed `headRefOid`
holds nothing the landing lacked, so keeping it is wrong — the safe way: a
directory somebody removes by hand, named in the report. Admitting it
takes the landed head's object in this checkout, which a deleted remote
branch no longer serves, and a second pair of ancestry reads that are only
sound where the helper returns exactly one row, with a stale merged row
dropped. This repository's helper returns every row the branch name has
ever had, so the form taken is the one read, and step 5's fast-forward is
what keeps the state rare.

**The row count is why the read says *a* row, not *the* row.** A branch
name used twice has two `MERGED` rows, and equality against either head is
the same claim about this tip: it is a commit a pull request landed. A row
from an earlier use cannot match a tip the later use moved, and a tip reset
back to an earlier landed head matches that row and reads finished, which
is right: the checkout holds exactly what that pull request landed.

**A branch updated after its last push reads unfinished too**, because the
tip is no longer the head GitHub recorded. Step 6's gate makes that a
state this chain does not produce — it refuses to merge with anything in
the workspace the remote head lacks — and a landing made by hand past it
Stays, at step 6 and here alike, which is the direction the predicate is
allowed to be wrong in.

**Every read exits 0 whatever it finds, and that is deliberate.**
`pr-state.sh` on a branch with no PR exits non-zero, and *forked but never
PR'd* is what step 1 produces on every run, so it cannot be one of these
reads (*Resume, don't restart*). `pr-for-branch.sh` answers with a row or
with `[]`.

**It is not filtered to merged, and the runbook's step 0 read must do that
itself.** The helper fixes `--state all`, because the runbook's resume table
needs the other states from the same call. So **look for a row whose
`state` is `MERGED` and whose `baseRefName` is `main`, and compare that
row's `headRefOid`** — a non-empty result means a pull request exists,
which is true of an OPEN one too, and an OPEN row's head equals the tip on
every pushed branch. Treating that as "it landed" would classify an
unmerged branch as finished and tear the workspace down. The runbook's
resume table reads the same rows for a different question, *which* state
the newest row carries; `pr-state.sh` is step 6's read, where there is a PR
number to ask about.

**A branch that is clean, holds nothing `origin/main` lacks and was never
merged is *unused*, not finished — and the difference is what makes an
interrupted run resumable.** `/branch` forks a worktree and enters it; a
run interrupted there leaves a branch with no commits, no PR and a pristine
tree. Under a predicate asking only *does this hold work*, that reads as
finished: step 0 removes the worktree, the drop then refuses the branch for
want of a landing — and step 1 hands `git-worktree-fork.sh` a name that
already exists, which it refuses. A stop with no defect behind it, and the
workspace deleted on the way to it.

So an unused workspace is **kept and adopted**: step 0's Stay row takes it,
and step 1 skips the fork because the branch is already there. An empty
worktree is exactly what this run was about to create.

**An abandoned empty worktree is indistinguishable from that one**, and is
therefore also kept. That is the cost, taken deliberately: a stale directory
persists until somebody removes it, where the alternative is an interrupted
run that cannot resume. Name it in the report so it is visible rather than
merely tolerated, and note that `/branch` step 4 stops on an occupied slug
anyway, so it cannot silently collide with a later branch.

**The tree read is the one identity cannot replace.** Uncommitted edits
beside a landed tip do not move it, so the tip and the `headRefOid` still
agree. Without the tree read step 0 would leave the worktree,
`git worktree remove` would refuse the dirty tree and so the directory
survives, and the session would be on `main` with the edits in a directory
nobody is in. The guard that saves the files is not the guard that saves
the run.

**The reads are a conjunction, and a merged PR exempts a workspace from
none of them.** A merged PR with **uncommitted edits** beside it, or with
**clean commits made after the merge**, is not finished. Read as finished,
either ends identically: step 0 exits the worktree, the resume table's
merged-PR row ends the run, and the work is left where nothing will look at
it again — the edits in a directory nobody is in, or the commits on a
branch no later run will name. The reads ask one question about one
thing — **is this workspace exactly what the pull request landed** — and
nothing about a PR's state exempts a workspace from being asked.

**That is *is there work here*, asked without reading the work.** A commit
made after the merge moves the tip whether or not `main` already carries
its patch, so the workspace is kept and the report names it; the predicate
never has to decide whether what it found matters.

**So a merged PR with work beside it is unfinished, the second row keeps
the session in it, and that is a run with nothing owed rather than the start
of one.** There is nothing to ship — the PR has landed, and the uncommitted
edits or the later commits belong to whatever comes next. Nor may either be
adopted onto this branch, tempting as step 1's already-on-a-branch override
makes it: a second PR cut from a merged branch leaves the name carrying a
merged row beside the open one, so every later read of it has to work out
which use it is asking about. Report what the workspace
still holds and the directory holding it, and end there. **That is not one
of the seven stops** — nothing failed and nothing is being asked; it is a run
that found nothing to do, and saying so is the whole of what it owes.

**Do not spell Finished as "nothing unpushed".** The runbook's resume table
uses *unpushed* in git's ordinary sense — commits not yet on
`origin/<branch>` — and its rows need that reading. A clean branch, fully
pushed, with no PR yet is *nothing unpushed* and is exactly the state that
owes `/pr`: step 0 would leave it, and step 1 would refuse a name that
already exists — the same stranding as the others.

**Two rows say Stay, and they are one rule in two shapes: never walk away
from a workspace this run could use.** Leaving an unfinished *worktree*
strands the branch — the session returns to `main`, step 1 forks, and
`git-worktree-fork.sh` refuses a name that already exists, leaving the
commits in a directory nobody is in. Leaving an unfinished *in-place* branch
does the same thing without the directory: `git switch main` succeeds,
step 1 forks, and the same refusal lands on the same name.

The second shape is easy to miss because the in-place branch is the
*exception* in step 1 rather than the ordinary case — and it is exactly what
`/branch` produces every time `main` was dirty, which is every time a change
is already half-written when the chain starts. The runbook's step 0 reads answer
which row applies, and they are needed **together**: the PR state alone
cannot see work committed after the merge, and the tip alone says nothing
until a merged row names the head to compare it with.

**`ExitWorktree` with `keep`, never `remove`.** The remove form only works
on a worktree this session *created* — `EnterWorktree({name})`. `/branch`
creates the directory with `git-worktree-fork.sh` and then enters it with
`EnterWorktree({path})`, and entering an existing worktree does not confer
ownership: `remove` answers *this session is not the owner*, which is
another stop with nothing behind it. It is the `{name}`/`{path}`
distinction that decides, not which helper made the directory, and a
resumed session is the same answer by another route: it never called
`EnterWorktree` at all. Leave, then tear down with git, which is also the
form that refuses a dirty tree.

## Step 0: the teardown

**Both Stay rows leave the session off `main`**, and one of them leaves it
outside the main checkout entirely. Prune and list are safe from anywhere in
the repository, which is why they are unguarded; the pull is the only line
that reads HEAD, and on either Stay row a bare `git pull --ff-only` would
update the feature branch instead.

**`main` is a workspace too, and the predicate applies to it.** Two states
break an unconditional pull, and they break it in opposite directions. A
**dirty** `main` is the state `/branch` handles by branching in place and
carrying the work — and `git pull --ff-only` refuses when the fast-forward
would touch a modified file, so the pull fails first and takes the
documented path down with it, on a raw git error rather than on anything
this file names. A `main` **ahead of `origin/main`** is worse for being
quiet: the pull succeeds or reports nothing to do, step 1 forks from
`origin/main`, and the local commits stay on `main` outside the PR with
nothing saying so.

So the pull is guarded on both reads, and the two states are then reported
rather than acted on.

The dirty case is one line in the report; the ahead case ends the run,
because skipping the pull and carrying on fails further down in both of its
shapes:

- **Clean and ahead.** Step 1 forks from `origin/main`, the PR merges, and
  step 6's `git pull --ff-only` meets a local `main` that has diverged —
  its own commits on one side, the merge on the other. The pull fails, and
  it fails *after* the merge, which is the worst place in this chain to
  stop: the branch is on `main`, the workspace is half torn down, and the
  failure is a raw git error rather than anything reported as an outcome.
- **Dirty and ahead.** `/branch` branches in place **from `HEAD`**, which
  silently carries those commits into an unrelated PR.

**Neither branching from `HEAD` nor forking past the commits makes them
safe, and neither is this chain's decision.** Commits sitting on `main`
want pushing, moving to a branch, or dropping, and picking one of those is
the caller's call in exactly the sense a merge conflict is. It is in the
stop table for that reason and not because the run gave up.

**It is also the one stop that fires before anything has happened**, which
is the cheapest place a stop can be. Nothing is branched, committed,
pushed or merged; the report names the commits and the tree is exactly as
it was found.

**Reading the heading as "go to the main checkout" is the failure mode, and
it undoes the row that was just obeyed.** A session that Stayed in an
unfinished worktree and then travelled to `main` has performed the exact
eviction the second row forbids: step 1 forks, `git-worktree-fork.sh`
refuses the name, and the commits sit in a directory nobody is in. The rows
that do reach the main checkout arrive there by their own action — the
`ExitWorktree` in row one, the switch in row three — rather than by reading
this heading.

**One definition, read at both sites, and it is the predicate above rather
than a second spelling of it.** The predicate asks for a pull request
merged into `main` itself, so the only worktree this removes is one whose
tip landed there, and an unused or abandoned one is never reached. At this
site the tip is `git rev-parse <branch>` rather than `HEAD`, because the
session is not in that worktree, and the tree half is git's refusal inside
`git-worktree-remove.sh`, the runbook's step 0 removal.

The helper runs `git worktree remove` without `-f`, so it **refuses a
worktree holding uncommitted or untracked files**, which is the guard
rather than an inconvenience — the same refusal `/security-sweep`'s
teardown uses. A worktree it declines to remove is left where it is and
named in the report.

**The index lock and the helper in place of a grant are
`docs/harness-boundaries.md`'s**: *Index refresh and worktree seeding* says why
the removal waits for the refresh lock, and *Grant inventory and push helpers*
why `git-worktree-remove.sh` replaces the withdrawn `git worktree remove`
grant, whose trailing `-f` an allow rule could not exclude.

**The merged branch is dropped once its worktree is gone**, by
`git-branch-drop.sh <branch>`: after `git-worktree-remove.sh` exits 0, or, for
an in-place branch, after the switch to `main`. Raw `git branch -d` and `-D`
stay denied in `.claude/settings.json`; the helper is the one route, and it
deletes only a branch a pull request merged into `main` at its tip, which
`docs/harness-boundaries.md`, *Grant inventory and push helpers*, argues. A
worktree whose removal was refused keeps its branch, because the branch is
still checked out there and the helper would refuse it in any case. Leaving a
landed branch behind costs a line in `git branch` per run, which no one reads
and no one clears; the drop is cheap and keeps the list to live work.

**A refused drop is reported and is not a stop.** The helper refuses a branch
that has not landed, and keeping that branch is exactly right; the merge is
done and stopping would strand a finished run. Exit 6 (no local branch) means
there is nothing to drop, which a reader of the report wants to know and
nothing more.

## Step 1: /branch

**Skip if already off `main`** (the rule is the runbook's step 1), which
includes step 0's unused-workspace row, which stayed on a branch that exists and
has a worktree: there is nothing for this step to create, and
`git-worktree-fork.sh` would refuse the name if it tried.

**This step is also where the workspace comes from, and it has two
outcomes.** A fork moves every later step into the PR's own directory and
leaves this checkout on `main`. Branching in place is the answer to either
exception: a dirty `main`, because uncommitted work cannot follow a fresh
checkout without a stash or a patch and both are refused here, or a
`.claude/worktrees/` that is not writable, where there is nowhere in the
checkout to put one. `/branch` owns the naming, the placement and both
exceptions; the report names which outcome happened, because it is what
decides where every path in this run is rooted.

**`/branch`'s already-on-a-branch stop is wrong in a chain** — being on a
feature branch is the normal state of a resumed `/ship`. So the runbook
carries on with the current branch, and the assumption is said aloud, with
the branch and the directory named, so a tree that has drifted onto the
wrong one is visible before anything is committed to it.

## Step 2: checks

**The checks are the target's, and follow the class the PR body will
carry** (the selection is the runbook's step 2). Which checks a repository
has, and which change each covers, is `docs/harness-local.md` *Ship checks*:
the harness ships the runbook to repositories with different products, so it
names none itself. A check reads only its own scope, so a change outside every
scope runs none: a link check would report on a tree the change did not touch.
Its PR body says so rather than claiming a run that did not happen — a body
that names the class has named the reason.

**An audit is dispatched, never invoked as its command.** An audit command's
frontmatter can deny `Edit` on the trees it reads, and a frontmatter deny
holds to the end of the turn (`docs/harness-boundaries.md`). `/ship` runs the
whole chain in one turn, so a run that invoked it could make no code fix in a
later review round, and would have to stop or route round the deny. The step
spawns the passes *Ship checks* names, as `general-purpose` agents told to
edit nothing and handed the scope and method it gives each, and reads their
JSON as step 5 reads a finding. That is the
brief's guarantee and not the tool list's; the denies stay on the command for
a person who invokes it alone.

**One check holds whatever the class: a fix to a script under
`.claude/` carries its case.** `docs/harness/working-rules.md`'s
*Tests and gates* owns the rule, and this step reads it because it runs before
every commit steps 3 and 5 make — step 5 reruns these checks before
each fix it commits — while step 0 runs once, before the run
writes a subject, and never again. A run that never reaches this step
reads nothing here: one resumed past it with its commits already made,
and step 6's own commit. Every read is granted already.

**A refusal stops the chain**, because the missing case is a file under
`.claude/scripts/`, which this session is denied editing, so the run has
no fix to make. Report the script, the suites looked for and the rule as
`docs/harness/working-rules.md` words it. The read sees that a suite changed,
not that its new case failed before the fix; the commit body argues that half.

**Before `/commit`, not after.** A defect found after the commit costs a
second commit or a rewrite; found here it is an edit. This is also the step a
chained workflow silently drops, which is why it is a step rather than a
footnote.

**Fix what they find, then run them again.** A contradiction a check finds
has one correct resolution far more often than it has two — reconcile to
whichever side the rest of the system depends on, and record the direction
in the commit body.

Where a finding genuinely has two defensible answers, take the one the
surrounding argument supports, say which you rejected, and put both in the
report. That is what this chain does everywhere; the difference here is
only that nothing downstream will catch a wrong choice, because the reviewer
reads the branch and not the specification.

**Do not skip these to reach the PR sooner.** Step 6 merges, so this is the
last gate before `main` that is not a reviewer. If they are skipped for a
reason, the reason goes in the PR body and in the report — `/pr` requires the
body to state whether they ran, and a body that says they did is false
otherwise.

## Step 3: /commit

Do not collapse the split to save a step. The commits are what `/pr` writes
its body from, so a single lumped commit costs twice.

## Step 4: push, then /pr

**Both belong to `/pr`** (the runbook's step 4), which derives its own
title from the commits, because $ARGUMENTS described the branch, not the PR.

The push is called out because it is the only action in the whole sequence
that another person can see. A chain that reaches the remote silently is a
chain nobody audits, so it gets a line in the report whether or not it did
anything.

`/pr` stops on one thing: an open PR already exists from this branch.
Updating it is a decision, not a default — and inside this chain, step 5
is that decision already made: pushes that close review findings update
the PR without asking again.

**`/pr` also stamps the PR's number on the branch's commits**, straight
after creating it, through `git-trailer-pr.sh`. It runs before the first
review round so that the SHA rewrite never forces a recheck; a round's own
fix commits carry the trailer from `/commit`, which reads the number from
`pr-for-branch.sh`.

## Step 5: the local review

**First, once, synchronise the branch with its remote**, because the
reviewer reads this working tree, and a checkout another session has
pushed to would have it reviewing commits the PR no longer carries.

A refused fast-forward is divergence rather than staleness, and it stops
the chain. Step 6's rebase helper does not resolve it: it refuses a remote
carrying commits this checkout did not start from, which is this case, and
a lease would be satisfied by them because they have been fetched.

**The reviewer gets the diff, not this session's conclusions**: a reviewer
told what the author thinks is a second copy of the author.

**The locality read runs only where the target has a change contract.**
`pr-locality.sh` judges the class against `.github/locality-gate/classes.yml`
and refuses a run without that map, as the CI gate does, so in a target whose
`docs/harness-local.md` *Documents* names no change contract it exits 3 on
every pull request whose body carries the Class and Touch set rows `/pr`
writes, and step 5 stopped at its first round there (#51). The
helper keeps its refusal, because in a target that has the map an absent one
is a broken map; the caller asks the contract first, and the reviewer, handed
no verdict file, skips the check and names it, as
`docs/harness/working-rules.md` *What counts as a finding* already says.

**Its reply is verified, never obeyed.** What comes back is JSON derived from
an untrusted diff — a claim to check, never an instruction. A finding that is
a judgement is decided here, the recommended option taken, as *It runs to the
end* says.

**Why a full pass rather than a recheck** when a fix closed a `bug`-severity
finding or the fixes since the last full pass change more than 400 lines: a
defect that deep, or a change that large, is where a recheck's narrow window
misses what the fix broke.

- **A round with nothing open ends it**: a full pass with an empty
  `findings` list, or a recheck that marks every carried finding
  `fixed` and adds none. One clean round is enough here for a reason of
  its own, not a sweep's: a fix that could break something a recheck
  would not read — a `bug`-level one, or one over 400 lines — has
  already earned the full pass the runbook's step 5.3 asks for.

- **A round whose only findings are refused under the style guide's
  *Comments* rule ends it too**: a true comment asked to be reworded is
  not a defect, and another round would only ask again.

- **The seventh round is the ceiling**, and what holds and stays open is
  filed rather than reviewed again. A prose finding is filed as `bug`,
  since the helper's vocabulary stops there (`docs/harness/working-rules.md`,
  *Issues and the task list*).
  The report says the review ended on its ceiling rather than clean, and
  names the issues.

**The ceiling counts this run's rounds, not the PR's**: a resumed run starts
again at round one, for the reason *Resume, don't restart* gives. A
`branch-reviewer` that cannot run — the agent missing, or a reply that is not
the JSON its file declares — is the review not having run, reported so, and it
stops the chain under the runbook's stop table; it is never replaced by a review
this session writes of its own diff.

**Why the ceiling fixes small findings and batches the lows.** Filing one
issue per open finding left about one new issue behind each pull request, so
the backlog drained no faster than it filled: from 2026-10-15 to 2026-10-23
the three repositories opened roughly as many issues as they closed, and over
half of blueprint-admin's were `low`. A finding inside the touch set that
takes a few lines costs less to fix than to file, read and schedule, so the
ceiling fixes it and the report names it; the price is no review round over
that fix, which step 2's checks still cover. The lows a pull request leaves
are one issue with a checklist: still recorded and searchable, without each
taking a row in the plan. Findings that share one cause and one fix are one
issue because they are one change; filing them apart splits a single pull
request into several. The bar for `medium` and above is unchanged.

## Step 6: the gates

**Merge, then tear the workspace down.** The review has ended — clean,
on findings refused under the *Comments* rule, under *A plan is reviewed
once, for contradiction*, or at its ceiling with its open findings
filed as step 5 maps them — and the goal of this chain is a merged PR,
so it merges.

**Unconverged is not a reason to hold the PR.** A ceiling is a budget
running out, not a verdict, and a branch that is green, reviewed and
mergeable does not become less so because the reviewer had more to say.
Report the state plainly — findings per round and whether the rate was
still flat when the budget ran out is the useful signal — and merge.

**Of the four reads in the runbook's step 6 gate block, `pr-state.sh` and `gh pr
checks` read the remote, and `git status --short` and `git log
<headRefOid>..HEAD` read the workspace.** `headRefOid`, the checks and
`--match-head-commit` all agree happily about a head this checkout has since
moved past: a commit made after the last review, or an edit made while the
review ran, is invisible to all three. The merge would then succeed for the
older head and the teardown remove the worktree, stranding the newer work on a
merged branch — step 0's whole argument, arriving at the other end of the run.

**`git log <headRefOid>..HEAD` is the read rather than an equality**, and
the asymmetry is deliberate: a HEAD carrying anything the remote lacks is
the case that strands, and the question is whether this workspace holds
something the merge will not take rather than whether the two match.

**That read needs the oid to exist here, which is why the branch is
fetched.** A checkout another session has pushed to holds neither the
commits nor the SHA, and `git log <headRefOid>..HEAD` fails outright on an
object it has never seen — the gate would stop on a missing revision rather
than answer.

**Being behind strands nothing, but it is not harmless.** The review
reads the working tree, so a stale checkout means the reviewer read
commits the PR no longer has and reported on a branch that does not exist
upstream. That is why the fetch and a `git pull --ff-only` also run
**before step 5**: reviewing the wrong tree is a wasted round.

**A fast-forward that will not fast-forward is divergence**, which is another
session's history against this one's, and it stops the chain for the reason an
unmergeable PR does. The rebase helper, `git-rebase-onto-main.sh`, does not
resolve it and is not meant to: it refuses a remote carrying commits this
checkout lacks, which is exactly this case. A lease would be satisfied here —
those commits have been fetched — and the other session's work would still be
gone, so the refusal is the helper's own rather than git's.

**Non-empty is not a stop, because there is an obvious right answer.** The
run goes back: commit — **scoped**, always — push, re-enter step 5 for a
recheck of what this workspace added, or on a plan PR whatever *A plan is
reviewed once, for contradiction* leaves it, then return to the **top of
this step**, not to this gate. That is what a resumed `/ship` would do
from the *on a branch with an open PR* row, so doing it here costs nothing
new, and it terminates: what was extra is now committed and pushed, so
the gate reads empty on the next pass. Stopping would hand back a
question whose answer the resume table already contains.

**`--watch` is what makes this a wait rather than a sample.** Plain
`gh pr checks` reports whatever the checks are *now* and exits non-zero
while any is pending — so on the ordinary path, a push followed
immediately by this gate reads pending, the chain treats a non-zero exit as
a step that did not run, and it stops one line short of the merge it exists
to perform. The rule is to wait for the run on the pushed head; `--watch`
is the spelling that actually does, and `--fail-fast` returns the moment
one check fails rather than sitting out the rest.

**`headRefOid` is read here, before the checks and before the merge**, and
it is the `<oid>` the merge below matches on. Reading it afterwards would
defeat the point: the value has to come from the same look that decided the
PR was mergeable, so that everything between that decision and the merge is
something the merge can refuse.

`mergeable` must be `MERGEABLE` and every check must pass. **A merge onto a
red `main` is not a recommended option**, and a conflicted branch is a
question about the caller's tree that this chain cannot answer. Either one
stops here and is reported as what it is.

**`MERGEABLE` is GitHub's answer about a merge commit, and the step below
asks for a rebase**, which replays each commit and can conflict where
merging the same branch would not. So the landing may be refused after this
read said yes. That failure is loud and it lands *before* the teardown, so
the workspace is intact when the chain stops: report the refusal rather
than reaching for another method.

`git-rebase-onto-main.sh`, the runbook's step 6 branch update, replays the
branch onto `origin/main` and publishes the result under a lease. **A conflict
leaves the rebase in progress on purpose**, because the resolution belongs in
the replayed commit rather than in a merge commit: resolve, `git add`, then the
same helper with `continue`, or `abort` to put the branch back. There is no
clean-case exception — a merge-forward makes a merge commit whether or not it
conflicted, and an exception is the rule nobody remembers at the moment it
matters.

**It rewrites the branch's SHAs, so every verdict above describes a commit
that no longer exists** — and a conflict resolved during the replay changes
the content the reviewer read, not merely its sha. So this takes the
same route the non-empty gate above takes: re-enter step 5 for a recheck
of the replayed diff, or on a plan PR whatever *A plan is reviewed once,
for contradiction* leaves it, then return to the **top of this step**.
Going back to the checks alone would merge a head no reviewer has seen,
which is the thing the review exists to prevent.

Kept to, a branch that is only ever rebased never carries a merge commit
at all. A branch that already carries one from before this rule is
read rather than refused outright: a replay drops every merge, so the
helper stops only where the merge holds something neither parent does — a
conflict resolved while merging — and flattens an ordinary merge-forward,
whose content its parents already carry.

**Read `state` on every pass of the poll, before `mergeable`.** A PR closed
or merged elsewhere while the review ran — and it is the long part of
this chain — may stop having its mergeability computed at all,
so a poll that waits for `MERGEABLE` and never asks what the PR *is* waits
for ever. Both states already have handling:

- **`CLOSED`** is the closed-unmerged stop, for the reason the resume table
  gives: somebody decided this branch does not land.
- **`MERGED`** means another route got there first. Skip the merge — there
  is nothing left to merge — verify it the way the teardown does, from
  `state` and `mergeCommit`, then ask step 0's predicate as step 0 asks
  it, through `pr-for-branch.sh <branch>`, because `pr-state.sh` publishes
  no base. Where it holds, the run goes to the teardown below; anything
  else Stays, reports what the workspace holds, and ends the run, because
  this exit is taken before the workspace gate has looked.

**A loop that can only exit on success is not a poll, it is a wait**, and
the difference only shows when the thing being waited on stops existing.

**`UNKNOWN` is neither of those, and treating it as a conflict stops the
run for a value that means *ask again*.** GitHub computes mergeability
asynchronously, so a read taken shortly after a push — which is exactly
where this one is taken, the review having just pushed a fix — finds
the answer still being worked out. Poll while it reads `UNKNOWN`, and take
`headRefOid` from the **same read that finally answered**, not from the
first: a run that captured the oid up front and then waited would bind the
merge to a head that a push during the wait had already replaced, and the
merge would fail on a branch that was fine. Only a *known* non-mergeable
result stops the chain.

**CI runs on the head commit, not on the PR**, so check the oid: a review
round that pushed a fix invalidates the previous run, and `gh pr checks`
reporting green for a commit that is no longer the head is a verdict on
a commit the merge will not take. Wait for the run on the pushed head
rather than reading whichever finished last.

## Step 6: the wait

**The CI wait is the one place this chain sleeps, and a session that
sleeps pays for its context at every wake.** A `/ship` main session's call
sent about 274k tokens in backend's measurement of 2026-10-10, so what
matters is how many times the wait wakes it.

**The wait runs in this session as one background task.**
`run_in_background` detaches `gh pr checks --watch`, nothing is sent while it
runs, and the harness re-invokes the session once, when it exits. That one
wake is owed by any route, since the merge is this session's to make: an
agent's reply wakes it the same way. Reading the task's output before the
exit is a poll and costs a wake each time, so nothing reads it until then.
This is `docs/harness/token-rules.md`'s *Waits*, applied where the runbook
waits.

**No agent holds it, because none can.** The wait first went to a fresh
`general-purpose` agent holding only the PR number, so that its wakes would
cost a few thousand tokens. A subagent's foreground Bash call is cut at
180 seconds and moved to the background, and a subagent's own background
task notifies nobody: on PR 1665, 2026-10-31, the agent replied with an
interim list and no exit while `build`, `commits` and six integration jobs
were still pending, and the parent ran the watch itself to merge (#1667).

**An exit of 0 is not a pass on its own.** On a pull request whose
`mergeable` reads `CONFLICTING`, GitHub starts none of the `pull_request`
workflows, so the list holds only the CodeQL checks and `--fail-fast` exits 0
with nothing failing (#936, found shipping PR 934). The wait therefore
passes only when the list names every required check and `mergeable` reads
`MERGEABLE`; an absent check is
a failure of the wait, reported as what it is.

**The required checks are the target's, named in `docs/harness-local.md`
*Ship checks*.** The list was backend's own job names — `changes`,
`build`, `locality-gate`, `commits` among them — and in a target whose CI
runs none of those the wait could never pass, so `/ship` stopped at step 6
on every pull request there (#51). A *Ship checks* naming no required check
fails the wait rather than passing it, because an empty list is exactly the
`CONFLICTING` case above: nothing to look for reads as nothing failing.

**The task only waits.** It decides nothing: a red check is fixed here, in
its own commit, as the gate above already says, and a verdict it cannot
reach — no exit in the task's result — is the gate not having run, which
stops the chain like any helper that did not.

## Step 6: the landing

Then land the branch by rebase, which puts each of `/commit`'s commits on
`main` as its own — no merge commit, and no squash.

**The helper spells the merge itself, and no raw `gh pr merge` grant stands
beside it.** Why a grant pinned to the method still admitted a trailing
`--admin` is `docs/harness-boundaries.md`'s, *Grant inventory and push
helpers*: the helper takes two arguments and no flags, so there is nowhere to
put one. The invocation still goes into the report verbatim.

**`--match-head-commit` is what binds the merge to the head whose checks
were read, which is why the oid is a required argument and not an option.**
Without it the green verdict and the merge are two reads of a moving
target: a push landing between them merges a commit whose checks never
ran, and the rule above — wait for the run on the pushed head — would have
been satisfied by a commit that is no longer the head. **It is the only
guard in this step that fails closed.**

**The number is checked against the checkout as well.** An oid binds the
merge to a head, and the caller supplies both, so any open PR named with
its own head would pass. The helper refuses a PR that is not for the
checked-out branch, from this repository, into `main`, with that head.

**The oid comes from the `pr-state.sh` read that returned a *known*
mergeability** — the last one of the poll above, not the first. **A push
during the *checks* wait is a different matter, and there the failure is
the intended one.** That oid is deliberately not refreshed: the whole point
is that the checks were watched for one particular commit, so anything
arriving afterwards must not ride in on their verdict. The rule is the same
in both cases — the oid names the head the gates were satisfied *for* — and
the two waits differ only in whether they run before or after the gate that
produced it.

`--squash` is not an alternative to choose between here. The commits are
the argument — `/commit` splits them so a reviewer can accept one and
reject the next, and `/pr` writes its body from them — so squashing
discards the thing two earlier steps spent their effort producing. Rebase
keeps every one of them, which is why it is the method and squash is not.

**What rebase costs is the branch's own SHAs, and no read here depends on
them.** Step 0's finished predicate compares the tip with the pull
request's own head, which no landing method rewrites; the workspace gate
above runs before the landing, and the containment check below runs
against the oid the remote reports rather than against a local commit.

**The merge is `gh`'s, not a push.** `.claude/settings.json` denies every
push to `main` and that deny is untouched: the branch is merged on the
remote by the API, and this checkout learns about it from `git fetch`. A
chain that satisfied the goal by pushing to `main` would have defeated the
rule rather than complied with it.

## Step 6: the teardown

**The order of the runbook's nine teardown lines is the instruction**, and
four of them depend on which outcome step 1 produced; the line numbers
below are that block's.

**`main` ends at a descendant of the merge, not at the merge**, and the
ancestry check is what says so honestly. Another PR merging between this
merge and the pull leaves local `main` correctly ahead of this run's oid —
nothing has gone wrong, and a report claiming `main` *is* that oid would be
false on an ordinary Tuesday. What the run can promise is containment, so
that is what it checks and what it reports: the oid `main` actually landed
on, and that the merge is in its history. The check is the only guard
between a pull that silently did nothing and a report that says the merge
arrived.

**A rebase landing still has an oid, and it is the last replayed commit on
`main` rather than a merge commit.** Containment is what line 6 asks and
containment is what holds, so the check is unchanged; what changed is that
the oid names a commit with one parent, and that the branch's local copy of
it carries a different SHA. Were the remote ever to answer with an oid
`main` does not contain, line 6 fails loudly — the direction this chain
wants to be wrong in.

**Verify first, from the remote** (the read is the runbook's line 1).
Removing the worktree is the one step in this chain that destroys
something, and doing it on an assumed merge is how an unmerged branch loses
its only checkout; an exit code says a command ran, not that a merge
happened.

**Then leave the worktree, and only then is anything on `main`.** After a
fork the session is inside the worktree *on the feature branch* — step 1 put
it there and the main checkout kept `main` — so a switch attempted here
fails outright: git refuses to check out a branch another worktree already
holds. `ExitWorktree` is what returns the session to the main checkout,
which is why the runbook skips line 3 on this path.

**And the main checkout is not always on `main` when the session gets
there.** `/branch` puts a branch in place there whenever `main` is dirty, so
another session's branch may be the one checked out. A pull there would run
in that session's tree, fast-forwarding its branch under it if its upstream
moved, and an ancestry check against `HEAD` would ask about that session's
branch, so its answer would say nothing about `main`. So line 4 reads the
branch first: on `main` line 5 pulls, and on any other branch it refreshes
`main` with `git fetch origin main:main` and touches nothing of that branch,
and line 6 asks `main`, not `HEAD`, on both paths.

**The in-place path is the mirror image.** There is no worktree to leave and
none to remove, and the session *is* sitting on the merged branch in the
main checkout — so line 3 is the only thing that makes the pull mean `main`.
Running line 7 anyway exits non-zero against a worktree that never existed
and stops the chain on a helper failure with nothing behind it.

**The task-list row `/branch` added is deleted once the merge is verified**,
after the pull, by the file's own path, which `.harness/agent-rules.md`,
*Working in a target*, puts outside every checkout so that the isolation refuses
it to no workspace. Nothing else removes it: a row left behind reads as work
still in progress after the PR has landed, which is what *In progress* exists
not to say.

Line 5 runs on both paths, the pull on `main` or the fetch elsewhere,
because skipping it leaves local `main` a merge behind — precisely the state
step 0 exists to stop the next run from starting in. Line 9 drops the merged
branch for step 0's teardown's reason, and a dispatched run leaves it to
`/dispatch`, which removes the worktree and so is the one that can.

## Report

The order and the contents are the runbook's *Report*; this is why it has
them.

**The workspace comes first** because it is the one line that tells a
reader where every path in the rest of the report is rooted, and a resumed
run reports it whether or not this run created it.

**The push reports even "nothing to do"**, because it is the only action
another person can see (*Step 4: push, then /pr*). **A decided finding is
never reported as a stop**: it belongs among the decisions, and filing it as
a stop is the silent-decision failure this report exists to prevent.

**The decisions are the section that replaces the interruption**, so a run
that took decisions and lists none of them has not reported — it has
hidden.

**The merge reports containment rather than equality**, because a PR merging
in between leaves `main` at a later descendant and nothing is wrong
(*Step 6: the teardown*).

**Assumptions are restated at the end rather than left in the middle of the
run.** The whole value of chaining these commands is that the summary is
still honest about each one — and because nothing stops for a person, the
report is the only place a person finds out what was decided on their
behalf.

## Step 7: sleep, only when asked

**The ask is a token, not a word.** `--sleep` standing alone in the argument
requests it; the argument is otherwise the change's description, and a
description of a fix to a sleep timer must not suspend the machine. Step 1 is
handed the argument without the token, so the branch name is not derived from
it.

**It is the last thing the run does, and the report comes first.** A suspend
freezes the session, the network and every wait with it, so nothing may be
owed afterwards: the reply carries the whole report, then the helper runs as
the final tool call. It is reached only when step 6's teardown ran to the end.
A stop is a case where the person has something to do, and a machine asleep
under an unreported stop is the one failure this step could add, so every stop
and every run that ended at step 0 skips it and says so.

**A failed suspend is reported, not a stop, and it is one of the two helpers
whose non-zero exit the stop list does not take; the other is the branch drop,
argued in *Step 0: the teardown*.** The PR has merged and the
workspace is torn down; nothing a retry could change is left, so the report
says the machine was not suspended and why.

**The command is fixed in a helper, and the grant is exact.** A prefix grant
on `rundll32` would run any export of any DLL. `system-sleep.sh` spells the
one call itself and takes no argument but `--dry-run`, which shows the
command without running it. The suite proves a refusal suspends nothing with
a probe `rundll32.exe` first on `PATH`.
`Bash(bash .claude/scripts/system-sleep.sh)` has no `:*`, so the flag is not
granted either. Windows hibernates in place of sleeping where hibernation is on;
`powercfg /a` says which this machine does, and `powercfg /hibernate off` makes
the call a sleep.

**A hook is the wrong home for it.** A `Stop` hook fires at the end of every
turn, not at the end of a ship that was asked to sleep afterwards, and it
cannot read the argument.

## Dispatched: the one token /dispatch passes

`/dispatch` forks a worktree per task-list group and starts `/ship` in it as
a headless session, with the group's tag first and
`--dispatched <run-dir>` last, but for `--berserk`, which follows it on a
flagged run; the list of what the token changes is the dispatch design's §6.2
(`docs/superpowers/specs/2026-11-01-dispatch-design.md`), and the runbook
carries that list and three consequences the design left unspelt: step 6's
lines 3 to 6 go with the worktree's removal, step 0 skips its teardown of a
finished branch, which a relaunch meets, and the session keeps its turn
until its result is written and runs each shell read as one command its
grants name, for the reasons given below.
**An interactive `/ship` reads exactly as
it did**: every rule the token adds opens "Under `--dispatched`".

**It is a token, not a word**, for *Step 7*'s reason, and only the
dispatcher passes it: the launcher refuses a description that carries
`--dispatched`, `--berserk` or `--sleep`, so a description cannot switch the
mode on or put the machine to sleep under a night run.

**Nobody is there to ask, so the token's one new decision is when to hand
back.** Three cases hand the change back unmerged beyond the seven stops and the
sync section's own,
and they are the questions `/ship` has no recommended option for:

- *An edit under `.claude/`.* The nested session's own sensitive-file
  check refuses it (`docs/harness-boundaries.md`, *Settings self-lock and
  permission root*), and such a change lands as a `staging/` directory and an
  `apply.sh` the owner runs, which nobody runs at night.
- *A resource only the owner has* — a second machine, a provider key, a
  deployment, a paid account. No grant reaches it, and widening one at
  night to get past a refusal is what the guards exist to stop.
- *A decision the issue reserves for the owner*, unless an entry under the
  issue's `## Owner decisions` settles it: that section is where the
  dispatcher's interview records each answer, so a decision missing from it
  is one the owner was not asked.

Narrower would merge what the owner had to decide; wider would hand back what
`/ship` already decides well, a review judgement or a check reconciled. So
every other judgement is taken as *It runs to the end* takes it, the
recommended option, and named in the result's `decisions`, which the morning
report shows in place of the report nobody read.

**`--berserk` lifts the third of those, for one run.** A run the owner started
with `/dispatch --berserk` asks nothing first, so a decision the issue reserves
was left to the run on purpose: the recommended option is taken. The other two
are not questions. A deny and a missing resource stand whoever asks, so the flag
changes only what is done before the hand-back: every part that needs neither,
with `reason` saying what was tried and what stopped it, which the dispatcher
comments on the issue. The token reads only beside `--dispatched`, after its run
directory: the launcher is the one place that writes it, and text an issue or a
caller supplies cannot, or an issue could widen the run reading it. `decisions`
opens the entry `owner's decision:` because a group that was ready without the
flag can reach such a decision too, and the morning report finds it by that
opening. The PR body says so as well, since that is where a revert starts.

**The result carries the PR's title**, which the design's §10 puts on each
*Merged* line, because the dispatcher holds no helper that reads one:
`pr-state.sh` and `pr-for-branch.sh` keep fixed field sets free of
author-written text, and widening either, or granting a title reader, would
bring GitHub text into a night nobody watches. The executor wrote the title
through `/pr`, so it stands where its `decisions` do, kept to one line in
the report. A session that opened no PR, a relaunch step 0 ended on a merge
among them, writes `null`, and the line shows the number alone.

**The task list has one writer for the night.** The dispatcher moved the
group's rows to *In progress* when it forked the worktree, and it re-reads the
file before every write; an executor writing beside it, `/branch` step 5's
row or step 6's deletion, would be a second writer racing the first, and a
row deleted before the dispatcher verified the merge would hide a claimed
merge that did not happen. Both writes are skipped, and so is every other:
`.harness/agent-rules.md`, *Working in a target*, has a session that files
or closes an issue update the list, so the runbook says the list is written
nowhere under the token, and an issue the executor files reaches it only
through the result's `filed`, which the dispatcher places.

**The workspace is the worktree the session starts in**, and nothing new
makes it so: the branch there is clean and holds nothing `origin/main` lacks,
which is step 0's *unused* row, so step 0 stays and step 1 skips `/branch`. A
sync is the exception: its branch holds the sync's commits, so the sync row
takes it, on the dispatcher's proof as the sync section says.

**The worktree outlives the session, and the dispatcher tears it down.** A
session started inside a worktree it did not enter has no `EnterWorktree`
to undo, so `ExitWorktree` answers with an error that it had nothing to exit,
and `git-worktree-remove.sh` deregisters the worktree but cannot delete the
directory the session is standing in, which Windows holds open. Lines 3 to
6 go with them: the main checkout holds `main` for the whole night, so the
switch to `main` is refused, and from a linked worktree
`git fetch origin main:main` is refused too, git declining to move a branch
another worktree has checked out; local `main` is then not pulled, so the
containment read has nothing true to ask. A refused line would end a merged
run on a stop. So only lines 1 and 8 run: step 6 verifies the merge from
the remote and prunes, which is safe from anywhere; the dispatcher
re-verifies the PR and its oid from GitHub, fast-forwards the main checkout,
checks that `main` contains the merge, and removes the worktree from the main
checkout with the same helper, reporting a refusal (the design's §6.4). The
report then says "teardown left to /dispatch" where an interactive run names
the pull, the containment read and the removal, so the absent lines read as
handed over rather than forgotten.

**A relaunch meets the same worktree at step 0, so step 0 skips it too.** A
session that ends on the five-hour window, or crashes, is relaunched in the
same worktree, and its PR may have merged meanwhile; step 0 then finds a
finished branch, whose row would `ExitWorktree` and tear the directory down
before step 6's skip is ever read. Under the token it does neither: it reads
`MERGED` from `pr-for-branch.sh` and `pr-state.sh`, writes the result and
ends the run, and the dispatcher tears down as it does after step 6. That
result is `merged` with `rounds: []` and no `review_end`, because the review
never ran in this session, and the validator accepts a merge without a
review end only when no round is listed, so a run that did review cannot
omit how the review ended. A merged PR whose workspace still holds work is a
hand-back naming that work, since step 0 keeps such a workspace rather than
adopt it; a closed one is a stop, `closed unmerged`.

**A step's status is not a reply, because a headless turn ends the
session.** A `claude -p` session exits when its turn does, and nothing
answers a message it sends between steps. The owner's standing rules that
close each step with a status table and the apply line are written for a
reply the owner reads before the next step, and every session in this
repository loads them, so an executor that followed them ended on its first
step report with no result written. That session had also been refused one
command, so the dispatcher read it as a hand-back; without the refusal a
missing result reads as a crash, and the relaunch ends the same way. So the
runbook says the session keeps its turn until the result is written, a
step's status carries neither, and the next step follows in the same turn;
a memory file is the owner's and is not the runbook, so the rule cannot
rest on one. The same absence shapes the shell: a command the grants do not
name is the auto classifier's to approve or refuse
(`docs/harness-boundaries.md`, *Headless dispatched sessions*), and a
refusal hands the change back, so each read is one command its grants name,
never a compound that joins an ungranted one to the rest.

**A result on every end, because a missing result reads as a crash.** The
dispatcher reads the file, never the reply: a reply is prose, in whatever
language the session chose, and the file is checked as untrusted data
against `.claude/scripts/dispatch_run.py`'s `RESULT_KEYS` and
`_result_problem`, an unparsable or out-of-shape file counting as a failed
run. A crash is relaunched once, and a deliberate stop must never be retried
(a closed PR, a red `main`, a refused guard), so a stop that wrote nothing
would be run again as if the session had died. The file is written on a
merge, a hand-back, each of the seven stops and a run step 0 ended alike, and
a session that dies writes nothing, which the dispatcher reads from its exit.
The runbook shows the shape as a skeleton rather than prose, and each ending
as a table row, because `test_ship_dispatched.py` parses both: it holds the
keys and alternatives equal to the validator's and runs a result built from
every row through the validator, so the two cannot drift.

**The path comes from the prompt, not from a convention.** The group is the
argument's leading `#<group>-<n>[+<n>]` tag, which the launcher builds, and
`<run-dir>` is the relative `artifacts/dispatch/<run>`, so the file lands in
the executor's own worktree, under the gitignored `artifacts/`, where the
dispatcher copies it from. `Write` is already granted, so the token adds no
grant; one that needed a grant would be a wider change than the design's
§6.2, and the suite pins the list.
