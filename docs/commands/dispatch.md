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
argues.

The dispatcher itself is the session the owner started, because two of its
jobs are judgement — the interview, and placing a filed issue in a wave and
a group by the task list's prose rules — and a script holds neither. What is
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

**The rebuild is judgement written as a file.** Step 0 re-cuts groups and
moves rows by the note's rules 1 to 10, which no helper can apply. So the
session writes the whole rebuilt list under the run, and `tasks rewrite`
replaces the live list with it only when the run was opened by `start`, the
live list is still byte for byte the copy `board --run` read, the owner's
note above the sections is unchanged, the result parses into groups, and
*In progress* is exactly as it was. A rebuild made from a stale read is
refused rather than merged, and is made again from a fresh `board`. The
interview's answers change the list by the same route, in the same step
(spec §5).

**In progress is never rebuilt.** At step 0 the run has moved nothing, so a
guard keyed to this run's groups would hold no row, and a rebuild could drop
or edit another session's, which the spec's §4 leaves alone. So the whole
section has to come through unchanged, whatever the run's state says: this
run's own rows change only through `move`, `done` and `back`, and a stale
row of another session's is named to the owner rather than deleted. The
rebuild's cleanup of closed and merged rows is therefore *Remaining*,
*Tracking* and *Paused* only.

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
closes reaches no `tasks place`, since its row comes from step 0.5's
rebuild or leaves with the closed issue, so `filed` records it in the
same list: a close comment is a comment on an issue, which the spec's
*Filed overnight* counts.

`--dry-run` is the builder's flag the spec's §3 allows: it runs step 0's
checks and `board`, says what the rebuild would change and what would
launch, and writes nothing to the list or GitHub, so the owner can see a
night before starting one.

## The interview

Every issue waiting on a human is asked about before the night, because
nobody answers at night and an executor that would have asked hands back
instead (spec §5, §7). The list of who is asked is the spec's §5;
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

When the night starts the owner is told how to stop it, that no other
session may write the task list, and how to watch an executor: `wake` and
`result` print each one's transcript path, and `watch_session.py` follows
it in another terminal, so watching costs this session nothing.

## Ready and free

A group is ready by the spec's §6.1, judged mechanically by `board` and
`wake` from the list and the open issues. A ready group is free when no
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

## What an executor returns

The executor writes `<group>.json` in its own worktree, and `result` copies
it into the run and checks it as untrusted data: a missing, unparsable or
mis-shaped result is a crash, never a success (spec §6.3). A claimed merge
is believed only once `verify` has read it from GitHub — merged, at that
oid, from that branch, into `main` — and the main checkout, pulled, holds
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

On every wake it absorbs the result, verifies a merge, places what was
filed, then asks `wake` what to launch; the outcome table is the spec's
§6.4. A hand-back or a stop goes back into its wave marked `OWNER` and is
not retried (spec §2); a crash is relaunched once (spec §6.4).

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

The report is `report`'s English facts written up in `--lang`, in the
spec's §10's six sections, with what only the session saw added: a refused
removal, a refused drop, a failed pull, a failed `record`. It ends with the
apply line every message here ends with.

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
shape, and `gh-issue-create.sh`'s.

The git grants that write are the fork, removal and branch-drop helpers and
`git pull --ff-only`. `git log:*` reaches `--output=`, an arbitrary file
write, which the guard hook refuses in every spelling but one the shell
computes at run time, as `docs/harness-boundaries.md`'s *Output deny and
shell-expansion residuals* states; the dispatcher's reads never pass it.
`Edit(artifacts/dispatch/**)` is for the question, answer, rebuild and
report files; it also reaches the helper-owned `state.json` and
`tasks.read.md`, which the session never edits, a gap outside the guards'
threat model (`docs/harness-boundaries.md`, *The guards' threat model*).
`pr-state.sh` is not granted: `verify` reads it inside the helper. Nothing
the night does widens a grant: a refusal nobody is awake to see is what the
guards are for (spec §2).
