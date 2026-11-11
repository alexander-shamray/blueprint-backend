# /dispatch — the argument

This file holds why `/dispatch` is shaped the way it is; the runbook is
`.claude/commands/dispatch.md`, and it wins where they differ. Read this
when the command is disputed or edited, not when it runs. The design is
`docs/superpowers/specs/2026-11-01-dispatch-design.md`, cited below as the
spec; its values are its own and are not restated here.

## Sessions, not subagents

An executor has to take a PR to a merge: wait on CI, run the solution's
tests and spawn its own `branch-reviewer`. A subagent can do none of that,
for the three reasons the spec's §3 gives, so each executor is a headless
session, `claude -p`, running `/ship` unchanged but for the one token
`docs/commands/ship.md`'s *Dispatched: the one token /dispatch passes*
argues, and `--berserk` beside it on a flagged run.

The dispatcher itself is the session the owner started, because two of its
jobs are judgement — the interview, and placing a filed issue in a wave and
a group by `docs/harness/task-list.md`'s rules — and a script holds neither.
What is
mechanical is a helper with its suite: `dispatch_board.py` for the task
list, `dispatch_run.py` behind the same command line for the run, and
`issue_decisions.py` for the issue bodies. Each prints a few lines, so a
night of wakes fits one context (spec §3).

## The dispatcher stays on main

Executors fork from `origin/main` and land onto it; the dispatcher's own
checkout is where it pulls each merge and checks that `main` holds it. A
checkout off `main`, dirty or ahead of `origin/main` would make that check
answer about the wrong tree, so step 0 refuses one before anything is asked,
as `/ship` step 0 refuses the last (spec §3). `git rev-parse --git-dir
--git-common-dir` printing one directory twice is what tells the main
checkout from a linked worktree, the same test `git-worktree-fork.sh` makes:
the fork and the removal helpers run only from there.

The executor never tears its own worktree down, because a session cannot
delete its working directory on Windows; the dispatcher removes it once the
merge is verified, and a removal the helper refuses is named in the report
(spec §6.2). It then drops the merged branch through `git-branch-drop.sh`,
which `/ship` under `--dispatched` leaves to it: the branch is checked out in
the worktree until the removal, so only the dispatcher can. A refusal is
reported and keeps the branch.

## One writer of the task list

From step 0 to the report the dispatcher is the list's only writer (spec
§4). That every write goes through `dispatch_board.py tasks`, never `Edit`,
is this design's own rule, taken when the plan was reviewed: the list lives
outside the checkout, so a file grant on it would be the one write this
command holds that no helper checks. Each `tasks` action re-reads
the file at the moment it writes, so another session's edit since the last
read is kept, not overwritten.

**What GitHub decides is a helper's; only the grouping is judgement.** Step
0 once rebuilt the whole list by the rules in a note at its top, deleting,
moving and adding rows by hand as well as re-cutting groups, and day
sessions wrote rows the same way: on 2026-11-11 a hand-written sync row with
a dash in its `#` cell stopped `board` on a target's list (#395). So `tasks
reconcile` now applies every rule GitHub's state decides outside *In
progress* — a closed issue's row deleted, a row moved by its labels, an open
issue with no row added unplaced — and `docs/harness/task-list.md` holds the
rules the note held. What is left, waves and groups, is written as a file:
the session writes the
whole regrouped list under the run, and `tasks rewrite` replaces the live list
with it only when the run was opened by `start`, the live list is still byte
for byte the copy `board --run` read, the text above the sections is
unchanged, the result parses into groups, *In progress* is exactly as it was,
and every issue's row is still in its section. A rebuild made from a stale
read is refused rather than merged, and is made again from a fresh `board`;
one that adds, drops or moves an issue's row is refused as `reconcile`'s
work. The interview's answers change the list by the same route, in the same
step, after `issue_decisions.py` has moved the row an `unblock` or a `close`
changed (spec §5).

**An exit 2 promises no more than that the helper refused.** The runbook says
only that, because a refusal can follow a write and no one sentence about
"nothing written" holds for every helper. `board --run` writes `tasks.read.md`
as soon as it has read the list, and the `bodies/` cache beside it, since the
list a board refuses is the one a rebuild must repair; `run` removes the
earlier session's files it would replace before its launch checks, then leaves
`<group>.refused.json`, which `result` reads; `wake` saves its usage reading to
`state.json` before it parses the list; and a `tasks` write refused after the
list changed leaves the list changed. What a refusal left is read from the
files, never assumed from the exit.

**In progress is never rebuilt.** At step 0 the run has moved nothing, so a
guard keyed to this run's groups would hold no row, and a rebuild could drop
or edit another session's, which the spec's §4 leaves alone. So the whole
section has to come through unchanged, whatever the run's state says: this
run's own rows change only through `move`, `done` and `back`, and a stale
row of another session's is named to the owner rather than deleted. The
cleanup of closed and merged rows, `tasks reconcile`'s, is therefore
*Remaining*, *Tracking* and *Paused* only, and `tasks sync`, which
`issue_decisions.py` runs after an interview's close, leaves an *In
progress* row too: the issue it closed may be one a day session is working.

**A list `board` refuses is still rebuilt from.** `board --run` keeps its
copy as soon as it has read the list, before it cuts a group, so a group
spanning two waves or two models, which it refuses, is re-cut by the same
rebuild rather than by a hand edit. That re-cut is written before `tasks
reconcile` runs, not after: each change `reconcile` makes is checked against
`board`'s parser, so on a list whose groups span it refuses at the first one,
and a later rewrite cannot bring its changes back, since `rewrite` refuses to
add, drop or move an issue's row (#399). A list that does not parse at all, a
malformed header or an *In progress* row naming two issues, still needs a
human: `tasks rewrite` parses the live list too, and *In progress* is never
rebuilt.

`--run` takes the run's name, as every other subcommand does, and is
resolved under `artifacts/dispatch/` to a run `start` opened; `board --run`
and every `tasks` action refuse any other, so neither creates a directory of
its own. A mistyped or path-shaped name is refused before anything is read
or written, so the copy and the issue bodies never land outside the run.
`done` and `back` touch only the rows this run's `move` recorded, never
another session's row that happens to carry the same group, and `done` only
once `verify` has passed.

`tasks place` adds a filed issue's row, its missing cells set to the
cautious value, `OWNER` among them, and records the issue in the run's
state for the report's *Filed overnight*. What the interview files or
closes reaches no `tasks place --run`, since the filing helper places a
filed issue's row and `issue_decisions.py` syncs a closed one's, so `filed`
records it in the same list: a close comment is a comment on an issue,
which the spec's *Filed overnight* counts.

`--dry-run` is the builder's flag the spec's §3 allows: it runs step 0's
checks and `board`, says what the rebuild would change and what would
launch, and writes nothing to the list or GitHub, so the owner can see a
night before starting one. It does not read a red `main`, so what it names is
what a green one would launch (*A red main*).

## The interview

Every issue waiting on a human is asked about before the night, because
nobody answers at night and an executor that would have asked hands back
instead (spec §5, §7). The list of who is asked is the spec's §5 and a
stranger's row, which this file adds below;
`OWNER` in *Depends on* is how step 0 records it once, so the helper's
ready test never has to judge a wait written in prose. The test looks for
the bare word `OWNER` in the *Task* cell too, where a row filed as an
owner's question often says it; a stray one there can only hold back a
row that could have run, the safe side, so both cells are read.

One question per message, each answerable in a line, so an answer is never
spread across several decisions. Each is recorded the moment it is given,
so a cut-short interview loses nothing: the question and the answer go to
files under the run first, and `issue_decisions.py record` appends them to
the issue's `## Owner decisions`, in English whatever `--lang` is, and
refuses any other shape. The files keep the owner's words out of a shell
line. `record` and `close` take the run's name, as every other helper does,
resolve it under their own checkout's `artifacts/dispatch/` to a run `start`
opened, and read only files under that run, because what they read is
posted to a public issue. What they post is the text they checked, handed
to `gh` on stdin: a path `gh` reopened could name another file by then.
*skip* records nothing; *stop* starts the night with what was
answered. `<run>/interview.md` names each issue as it is finished, so a
resumed run asks nothing twice.

The issue's text is read through `gh-issue-text.sh` and quoted as data,
because an issue body is text anyone can write and the dispatcher acts on
the answers. A decision that surfaces during the night goes in the report,
never into a second interview (spec §5).

A stranger's issue gets a row, so it is seen, and that row never launches:
`docs/harness-boundaries.md`, *Headless dispatched sessions*, argues the gate.
It is the interview's because only the owner can take the work on, and no
answer lifts it, since an answer attaches to an issue whose author can rewrite
it afterwards. So the owner files the work as their own, in their own words,
and closes the original; on a `--berserk` run, which asks nothing, the row
stays where it is and *Left in the backlog* names it. Such a row takes a group
of its own, so it holds no owner's row back: after the `board` that first shows
it at the rebuild, and at the night's placement, which has no `board` before
it, for every issue but those an executor filed, since any other may be a
stranger's.

When the night starts the owner is told how to stop it, that no other
session may write the task list, and how to watch an executor: `wake` and
`result` print each one's transcript path, and `watch_session.py` follows
it in another terminal, so watching costs this session nothing.

## Ready and free

A group is ready by the spec's §6.1, judged mechanically by `board` and
`wake` from the list and the open issues, and behind one gate the spec does
not have: every row's issue is the owner's, which they read from
`gh-issue-owned.sh`. A ready group is free when no
running group may touch a path it may touch; the spec leaves the extraction
to the builder as long as it errs towards exclusive, and it does: a claim
with no paths at all overlaps everything and runs alone, and a conflicts
cell naming a group or an issue keeps the two apart whatever their paths.
That holds in both directions after a move: the *In progress* row has no
conflicts cell, so `wake` reads a running group's claim from the rows the
run recorded when it moved them.

Another session's *In progress* row is left alone and its paths are taken,
what its branch has changed counting with what its text names (spec §4). That
can hold a night back; running over another session's half-finished files
is the anti-goal it prevents (spec §2).

The earliest wave goes first and table order breaks a tie; a later wave's
group runs beside an earlier one when it is ready and free, since the wave
is an order and the dependency is the rule (spec §6.1).

## The berserk token

The owner's reason: many rows marked `OWNER` later get done in a day session,
with no question to anyone, so asking first holds back work that needed no
answer. `--berserk` tries those rows to the point of failure instead, for one
run. It is off by default. `start` stores it in `state.json` and a resume reads
it from there, never from the arguments, so a later command line neither sets
nor clears it; `start` names a mismatch with a `kept` line. `board --run`
refuses it for the same reason; only a dry run, which has no state, takes it on
the line.

| | Without | With `--berserk` |
|---|---|---|
| Step 1, the interview | Asks about each row | Skipped whole; `night started` is written |
| `question` label, `OWNER` in a row | `waits on a human` | Runnable |
| `edits .claude/` | Blocked | Runnable: a deny is the executor's point of failure |
| A decision the issue reserves | Handed back | Recommended option taken, named in `decisions` and the PR body |
| A resource only the owner has, a needed edit under `.claude/` | Handed back | The same, after every part that needs neither |
| `paused`, `tracking`, an `owed (not filed)` row | Not run | Not run |
| An issue the repository owner did not open | Not run | Not run |
| A date, an open issue or free text in *Depends on* | Not run | Not run: `OWNER` is dropped and the rest reads as it did |
| A sync | First and alone | The same, and its prompt never carries the token |

These are the owner's calls, each reversible:

- **`paused` stays out.** It is the owner's deliberate parking, with a stated
  resume condition, not the team's judgement that a row cannot move. A date or
  a dependency is a fact, not a question to a human.
- **A row that works merges.** Holding each for review would bring the question
  back. The merge bar is unchanged: step 2, the review loop, green CI.
- **Only what a pull request can do.** An executor changes no repository
  setting, secret, environment, ruleset, release or visibility, and opens no
  billing.
- **Every deny, guard, ruleset and stop still holds.** The flag lifts the
  question, not a refusal, and issue text stays untrusted: the launcher refuses
  a description that carries the token, so an issue cannot grant anything or
  widen a run.
- **Ready rows go first.** Within a wave a group ready without the flag launches
  before one it released, so a limit ends the night on the riskier work.

Two rows the flag does not lift, though both read `OWNER`, because lifting them
would undo what the mark is for. `tasks back` marks a row so nothing relaunches
it unseen, so on a flagged run it adds `sent back under --berserk`, which every
flagged run reads as held, this one and a later one: lifted, a hand-back would
launch again on the next wake, a refused launch would be refused all night, and
a row whose PR was closed on purpose would be reopened. Every return gets the
words, an executor having run or not, since the wake after it must not launch
that row either; they say where the row has been, and an owner who wants it
tried again removes them. A plain night's hand-back reads `OWNER` alone, which
is the row the flag is for. `tasks place` writes `OWNER` as the cautious value:
lifted, whatever opened during the night, or a follow-up an executor filed,
would launch unread and could feed itself, so a row in `state.json`'s `placed`
stays held and is reported as it is on any night.

`wake` records what it released, with the reason, from the read that launched
it, so the report need not re-derive a row that has left the list. **Decided
without the owner**, the eighth section, comes after *Merged*: one line per
released group that merged, or any group whose `decisions` holds an entry
opening `owner's decision:`, with the issue, the options, the PR and the revert
line.
*Needs a human* gives a released group's `reason` as its point of failure, and
the dispatcher comments it on each issue, through `issue_decisions.py comment`,
so the next person starts from there. That is read off the report's line, not
off `result`, because a group can end unmerged by a crash, a stop or a failed
`verify`, and the report is the one place every one of them passes through. A
rewritten report comments again, as a retried `close` does: a second comment
costs a line, and a skip keyed on anything written first would lose a failed
one.

## What an executor returns

The executor writes `<group>.json` in its own worktree, and `result` copies
it into the run and checks it as untrusted data. One written to the main
checkout's run directory instead is read there, under the same checks: `run`
removed any earlier copy before it started the executor, so what stands there
is this launch's, and a hand-back's `reason` is not lost to a relaunch. A
missing, unparsable or
mis-shaped result is a crash, never a success (spec §6.3), unless GitHub shows
the merge at the worktree's tip (*Waking*). A claimed merge is believed only
once `verify` has read it from GitHub — merged, at that oid, from that branch,
into `main` — and the main checkout, pulled, holds
it. A read that fails leaves the claim to be checked again; a contradiction
is a stop. The schema is `dispatch_run.py`'s, and `/ship`'s runbook shows
the executor its shape.

A relaunch onto a PR that already merged returns `merged` with no rounds
and no review end, because the review ran in the session before it; the
dispatcher verifies it like any other merge. `result` believes that of a
relaunch, a group whose earlier session this launch has already read, and
of a session whose own transcript, the one its envelope names, shows a
`branch-reviewer` spawned and not refused: a session that re-enters `/ship`
after a compaction reports only the rounds it ran since. Otherwise a merge
with no round is one nobody reviewed, so it still verifies and lands, but
the report lists it under *Needs a human*, never *Merged*; one the
transcript cleared is listed under *Merged* as reviewed by the transcript.

## Waking

Each executor's `run` is one background task, and its exit is the only
wake (`docs/harness/token-rules.md`, *Waits*): the dispatcher never polls,
sleeps or reads a transcript, because each wake re-sends its whole context. A
five-hour window is one background `wait` too, which ends early on the stop
file, and then the same group is relaunched in the same worktree (spec §6.4).

The dispatcher forks each worktree itself, from the main checkout, before
`tasks move` and `launch`: `launch` trusts only a worktree git shows on the
branch the *In progress* row names, so the order is fork, record, launch.

`run` hands each executor its own temp directory through `TMP`, `TEMP` and
`TMPDIR`, a new one per launch made by `mkdtemp` under the system's:
executors that shared the system's one ran each other's scratch scripts
(blueprint-harness#199), and a fixed path in a shared temp root is one another
user could plant. It stays outside every checkout, because
`refresh-index.py`'s worker runs from it and the guards admit a mutation
proof's scratch checkout only under it; `run` removes it, best effort, once
the executor exits or fails to start (blueprint-harness#205). After an
executor it then runs `git worktree prune`, as best effort: a `git worktree
add` the session made under that directory has lost its target, and would stay
listed until something pruned it.

On every wake it absorbs the result, verifies a merge, places what was
filed, then asks `wake` what to launch; the outcome table is the spec's
§6.4. A hand-back or a stop goes back into its wave marked `OWNER` and is
not retried (spec §2); a crash is relaunched once (spec §6.4).

A crash that merged is not one. An executor that wrote no result, or none
valid, and whose worktree meets `/ship` step 0's finished predicate — clean,
its tip the head of a row `MERGED` into `main` — has nothing a relaunch could
do but read the merge, so `result` takes it as `merged-claimed`, from GitHub's
fields and with `verify` still to come, and its `decisions` say the result
came from there. The rounds it ran went with the file, so a first session goes
to *Needs a human* as a merge with no review round unless its transcript shows
a reviewer, and a relaunch reads as one onto a merged PR does (*What an
executor returns*). A read that fails, or a predicate that does not hold,
leaves the crash as it was, and so does a sync whose tip is not the head its
proof bound (*The proof binds*). Run 20261108-071135 relaunched five groups of
twenty-one: one had merged and written its result to the main checkout's run
directory rather than its worktree's, which `result` did not read then and
does now (*What an executor returns*), and four had ended the turn on
`/commit`'s report, before any merge, which only a relaunch can recover. A turn
ended there ends a `claude -p` session, which is
why `/ship --dispatched` says a step's status is not a reply.

A relaunch is told what its earlier session left. Night 20261109-205859 lost
three executors that way again, two of them after a commit and one after
opening its PR (blueprint-harness#378). `/ship`'s resume rows read that
state already, but the relaunch's prompt was the first launch's word for
word, and nothing in it said a session had run and stopped short. So `run`,
on a group whose
status is a relaunch's, reads the worktree — commits ahead of
`origin/main`, an uncommitted tree, and an `OPEN` row from
`pr-for-branch.sh` — and adds what it found to the task as one clause
saying to resume from there and to end the turn only on the result. Each
read is best effort: one that fails drops its part, and `/ship`'s own state
read still decides what is owed, so a missing clause costs only the hint.
A sync's relaunch carries none, since its tree is held to its proved head
and never committed to, and the clause is built from the branch name, a
commit count and a PR number, never issue text.

A `Model` cell `launch` would refuse holds its group at `board` and `wake`,
since `launch` comes after the fork and `tasks move`, and a refusal there
leaves an empty worktree behind.

A `run` that starts no executor — no `claude` on the path, a stale file it
cannot remove, the stop file — would otherwise hold its slot all night,
since `result` has no session to read. So it leaves `<group>.refused.json`
in place of a session record, never a write to `state.json`, which is the
dispatcher's alone, and `result` records the group `crashed-before-launch`,
which frees the slot and goes back like a hand-back: whatever refused one
launch would refuse the next. A relaunch removes the session record it
follows, already read, before its launch checks; where that removal is
itself refused, the record stays, and one already read is treated as
absent, since it ended an earlier session rather than this launch. A group
sent back and launched again in the same run starts afresh, so `launch`
removes the earlier launch's marker, result copy, session record and pid
file, and is refused where it cannot: nothing then names that record as
read, and a later marker beside it would be refused as unread. The
marker sends a group back unread, so it counts only when no unread session
record sits beside it and it is no older than the launch, and an earlier
launch's marker never ends a later one; a marker beside an unread record is
refused, since either could be the one that does not describe this launch.

A run directory that refuses the marker leaves neither file, and `result`
alone cannot tell that from an executor still running, so it refuses, and
the group would again hold its slot all night. `run` names both refusals
on its exit line, which the runbook hands to `result` as `--refused` after
every exit 2; `result` reads it only when it finds no marker from this
launch and no session record it has not already read. `run` removes an
earlier pid file before its launch checks: with none, `result` records
`crashed-before-launch`; with one whose process has ended,
`crash-hand-back`, since the executor ran and may have merged and only its
worktree says so; with one still alive, it refuses. A refusal before that
removal, or of it, leaves the earlier pid file, which reads as an executor
that ran: a hand-back, the cautious side. Recording either writes
`state.json` in the same directory, so a directory refusing every write
still holds the slot; what this separates is a marker that only another
process holds.

## A red main

An open `main is red` issue stops every dispatched executor at `/ship` step 6,
whatever its own checks read, and a run's leading tag is not the argument that
exempts the run answering it (`docs/commands/ship.md`, *Step 6: the gates*). A
group launched beside one spends its session and goes back. On
blueprint-backend's run 20261109-093606 a group stopped there after 15 million
tokens and about $10.53, every check on its pull request green, and `wake` then
named the next group to the same stop, which the dispatcher held by hand.

So `wake` reads `main-red-open.sh` once it has a group to launch, and prints
each open issue and `launch: none` in place of the launch. It reads nothing
where no launch is at stake, so a drained night stays `end: drained` and a
failed read cannot cost it that ending. A read that cannot say `main` is green
is a refusal, which launches nothing, as a failed read of the open issues is.
A held launch records no release, since *Decided without the owner* names what
the flag released from the one read that launched it. `sync-ready` and
`sync-launch` read it too: a sync's executor takes the same stop after its
round, so its refusal waits for a later exit, as one naming a running group
does.

It holds and does not end the night. A running executor is left to its own step
6, and each exit reads again, so a `main` turned green launches what was held.
With nothing left to wake the session the night is over (*When the night
ends*), and the report names each issue under *Needs a human*, because a backlog
left unstarted would otherwise read as nothing being ready.

**The answers are the owner's.** `docs/harness/working-rules.md`, *CI*, says a
rerun comes first and an issue closes on its green run. The dispatcher does none
of it: it holds no grant for `gh run rerun`, raw `gh issue close` is denied, and
nothing the night does widens a grant (*Grants*), while a revert is a landing
and which of a red `main`'s answers lands first is the owner's. So the owner
reruns the failed jobs with `gh run rerun <run id> --failed`, comments on the
flaky tests tracker and closes the issue on a green run, or reverts, and the
next `wake` launches. Giving the dispatcher the rerun was the other answer, and
it would save a night a flake holds; it is rejected because it needs a new write
grant on a session nobody is watching.

`launch` reads it too, so resume's `moved, never launched` cannot start a group
an earlier `wake` named before `main` turned red: it refuses, and the
dispatcher puts the group `tasks back` as for any refused launch, so its rows
return marked `OWNER`, no later `wake` launches it, and its worktree stays for
the owner. That group is not held for a green `main`; the alternative, leaving
it moved so the next resume offers it again, is rejected because no `wake` line
would launch it in the meantime. It reads after the group's own checks and
before anything is written.

Three residues are accepted. A relaunch reads nothing, since its group already
holds its worktree and its session's work, and a hold would leave a running
group that no `wake` line relaunches. `--dry-run` reads nothing either, since
the dispatcher holds no grant on the helper and a read through
`dispatch_board.py` would only preview what the night may hold. And the signal
lags as step 6's does: the issue opens only once the push run has finished.

## Issue text stays off the command line

A group's task and a placed row's cells come from issue titles, and anyone can
write one. On a command line that text meets a shell, which runs a backtick
pair or a `$(…)` inside double quotes, on a line a prefix grant has already
approved. So it reaches the helpers as files under the run, written with the
`artifacts/dispatch/` grant: `run` reads `<g>.task.txt` and takes no text
argument, which also keeps a relaunch's task file the first launch's, with
only `run`'s own relaunch clause after it (*Waking*), and `tasks
place` reads `place-<n>.json`. The task is written before `tasks move`, so a
resumed night finds it wherever the dispatcher stopped. `--refused` carries
the helper's own line, in single quotes. `guard-helper-argv.py` is the
backstop for a session that spells any of them otherwise
(`docs/harness-boundaries.md`, *Prefix grants and wildcard denies*).

## Sync pull requests

A sync changes `.claude/` for every group after it, so on a night that starts
with no group active one found at step 0 goes first and alone: while it runs
nothing else launches, and a group launched after its merge forks from the new
`main` and runs under the new settings. That costs one executor's time, about
15 minutes, once a night. `wake` holds the others at `launch: none` while a sync
group is active, without ending the night.

A sync opening mid-night, or found by a resumed run with a group active, gets
neither promise. `sync-launch` refuses to start beside a running group, and
`wake` goes on launching while no sync is active, so it waits for the running
groups and new launches can overtake it, perhaps to the end of the night. The
cost is that those groups run under the settings the sync would replace.
Holding a slot for it would stall the backlog for a change nobody is waiting
on, so nothing reserves one: the morning's list shows it, and the next
`/dispatch` that starts with no group active takes it first.

**A wait leaves nothing behind.** A `blueprint-harness` sync run, every four
hours or on dispatch, opens a new sync where the payload moved and closes the
one before it, so a sync that waits may be superseded overnight. `sync-ready`
therefore asks `sync-launch`'s checks that read no tree before
`git-worktree-sync.sh` makes a worktree and a branch for a launch that was
never going to happen. `sync-drop`, before the
report, offers `git-branch-drop.sh` every local branch named as a sync head:
what merged goes, as does one whose pull request closed with origin's branch
gone at this tip, which no merge can land and which a worktree does not hold.
It first offers `git-sync-head-drop.sh` every branch on origin named as a sync
head, since `harness_sync` deletes a superseded one and nothing deletes a merged
one: the repository's delete-on-merge would, but that is the owner's setting.
Each head costs GitHub reads and a push, so the first run over a backlog is
slow, which is why step 3 gives it a ten-minute `timeout`; it is idempotent, and
a run cut short leaves the rest for the next.

**The proof binds.** The verify at step 0.3 and after each exit runs in the
main checkout, because the executor runs that head's own copy of `sync_pr.py`,
and a head cannot vouch for itself. `--oid` carries the proof to the launch:
`sync-launch` writes nothing unless git shows the worktree on that branch, at
that oid, and clean, and it records the oid. The executor then takes `<oid>`
as `HEAD` (`docs/commands/ship.md`, *A sync pull request is reviewed once, for
this target*), so `run` compares again before every relaunch: a tree a crash
left somewhere else is refused and goes to `sync-close`, even where step 6's
rebase moved it and the merge then landed. Nothing else can tell that crash
from an unproved head, and the report's *Syncs* line says which tree it was.

**No row.** No task-list row ever held a sync, and the next sync supersedes
it, so the report carries it, in the seventh section. `sync-<n>` names a group
in the run's state alone: `tasks` still takes only a letter and a number, so
nothing places or moves one, and a conflicts cell cannot name one. Its record
has no `rows`, which keeps it from `wake`'s running groups and from `tasks
done` and `back`, and empty `keys`, so it never holds a reported run open.
`sync-close` takes their place, runs after the teardown on a merge, sets
`closed` and keeps the outcome, so `report` does not list a merged and
confirmed sync as not confirmed in main. One left `verified`, by a failed pull
or containment read, is named as merged and not confirmed, and claims no rows.
A closed sync a human owes, each hand-back kind, is under *Needs a human* as
well as *Syncs*, since its worktree waits for the owner; only one stopped by a
closed pull request is left out, which the next sync supersedes.
*Resume* has lines of its own for a sync, because `done`, `back` and the
window's `wait` lead to `tasks`. A sync whose worktree is gone has no other
way out, so `sync-close` takes it once no executor is alive and no exit is
unread, as `crash-hand-back`: how it ended is unknown, so a human reads it.

**Sonnet.** `verify`, CI and the `branch-reviewer` catch its errors, and the
reviewer runs on its own profile's model (`docs/harness/token-rules.md`,
*Which model a subagent runs on*).

**Each exit lists again.** A sync's outcome can change what is open, so the
reads after an exit are `list` and `verify` as well as the issues.
`sync-launch` refuses a group this run holds, as a hand-back is never
relaunched, so a sync that stopped on a closed pull request, or was handed back
with its pull request still open, cannot loop; a new `take` is a new group. A
later night's re-take finds the worktree and branch the hand-back left, which
`git-worktree-sync.sh` refuses, so the dispatcher skips the helper and
`sync-launch`'s checks decide.

## When the night ends

The spec's §8 ends the night: the week's limit, nothing ready and nothing
running, or the owner's stop file, `<run>/STOP`, which `wake` reads on
every wake. Each ends it the same way: launch nothing more, let running
executors reach their own end, then write the report. A relaunch is a
launch too, so `run` and `wait` read the stop file as well: a crashed or
rate-limited group the owner stopped goes back to its wave rather than
starting again. A crashed one's `run` refuses and `result` sends it back;
a rate-limited one's `wait` prints `stop file: yes` and leaves it `window`,
so the runbook sends it back with `tasks back`, or the report would list it
as interrupted.

A night where `wake` names nothing to launch and no background task of
this session is left to wake it is §8's nothing running, read from this
session: what still holds a slot is an earlier dispatcher's executor, whose
exit wakes no task here, so nothing left can change before the report. That
group is reported as interrupted.

`end: held` is §8's nothing running too, with one difference the report must
not hide: a group is ready, and only an *In progress* row holds it, another
session's or one this run could not close. Nothing this dispatcher waits on
will free that row, so it cannot wake past it, and `wake` says `held`
rather than `drained` so that a blocked backlog is never reported as an
empty one.

No script can read the weekly figure, so `usage` says `not read` and the
limit is ignored, by the owner's decision in the spec's §8. Token Weather's
pause notice can still reach this interactive session; when it does it is
that limit, read the one way available, and ends the night as §8's weekly
stop does.

An executor's row under *Usage* sums `modelUsage`, which holds the whole
session, one entry per model and its subagents included. The envelope's
`usage` holds the last model turn alone, 71399 tokens of a session that spent
1662280, and it looks just as plausible, so it is read only for an envelope
with no model counts and the row says which of the two it came from. The row
carries `total_cost_usd`, and a session on more than one model has a line per
model, since a Sonnet executor's reviewer runs on Opus. A session whose
record was read but left no envelope, none of the three, spent an amount
nothing records, so the row ends with how many sessions it did not count, and
a group whose every such session left none still gets a row saying so: a
relaunched group's figure would otherwise read as its whole cost. A session
with no record at all (`absorb`'s `_unrecorded`, a `run` that exited refused)
is not counted: `result` prints `transcript: not found` for it, and the
report lists the group under *Needs a human* as a crash hand-back or a
pre-launch refusal, adding nothing to the row.

The report is `report`'s English facts written up in `--lang`, in seven
sections, the spec's §10's and *Syncs*, eight on a `--berserk` run (*The
berserk token*), with what only the session saw added:
a refused removal, a refused drop, a failed pull, a failed `record`, the
superseded rows `list` names and a failed verify. It ends with the apply line
every message here ends with.

## Resume

All the run's state is under `artifacts/dispatch/<run>/`, so a dispatcher
that ended — the week, a crash, a closed terminal — loses only time (spec
§9). `start` names an open run rather than opening a second, `interview.md`
says how far the interview got, and `resume` says what each unfinished
group needs next. It adopts only groups this run recorded and refuses one
whose executor is still alive, so it never relaunches over a live session
or a worktree it never forked.

**A report does not close a run that still holds rows.** Step 3 writes
`report.md` on every night, the interrupted ones included, but a run's *In
progress* rows change only through its own `done` and `back`, and a fresh
run would read them as another session's claim. So `start`, given the
task list, reopens a reported run while *In progress* still lists the keys
it recorded for a group it has neither done nor sent back — an interrupted
one, a merge not confirmed in main, a hand-back not yet sent back — and the
report's *Interrupted* ends with the resume line exactly then. The list is
the test, not the run's own statuses: a group whose rows the owner removed
by hand, which `done` and `back` would refuse, holds nothing and closes the
run, and an active group whose worktree is gone holds the run only while
its rows are listed, the report naming it under *Needs a human* so the
owner can clear them. A list `start` cannot read keeps the run open and
says why, since closing on a guess could leave rows to no run. The resumed
night writes `report.md` again, over the first.

## Grants

The command grants no agent type and denies every one, the profiles under
`.claude/agents/` and the built-ins alike, because it spawns sessions, not
agents (spec §11); `test_command_grants.py` reads the profiles on every run,
so one added tomorrow fails until it is denied here too. Leaving one out
would refuse nothing: a grant only auto-approves, a tool the frontmatter
does not list stays callable under the permission settings, and only a deny
refuses (`docs/harness-boundaries.md`, *Grant semantics*). `claude` is denied
as a shell command: the only launcher is `dispatch_board.py run`, which
passes the settings' own permission mode and never a skip flag (spec §6.2).
Raw `gh issue edit`, `close` and `create` are denied, because the issue
writes are `issue_decisions.py`'s, which accepts only the decision entry's
shape, and `gh-issue-hand.sh`'s.

The git grants that write are the fork, sync worktree, removal and
branch-drop helpers and `git pull --ff-only`. `sync_pr.py` is granted for
`list` and `verify`, and nothing in it writes to GitHub. `git log:*` reaches
`--output=`, an arbitrary file
write, which the guard hook refuses in every spelling but one the shell
computes at run time, as `docs/harness-boundaries.md`'s *Output deny and
shell-expansion residuals* states; the dispatcher's reads never pass it.
`Edit(artifacts/dispatch/**)` is for the question, answer, rebuild, task,
placement and report files; it also reaches the helper-owned `state.json` and
`tasks.read.md`, which the session never edits, a gap outside the guards'
threat model (`docs/harness-boundaries.md`, *The guards' threat model*).
`pr-state.sh` is not granted: `verify` reads it inside the helper. Nothing
the night does widens a grant: a refusal nobody is awake to see is what the
guards are for (spec §2).
