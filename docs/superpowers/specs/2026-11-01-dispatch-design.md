# `/dispatch`: an unattended backlog run — design

Date: 2026-11-01. Status: draft for the owner's approval. Owner of the
contract once built: `.claude/commands/dispatch.md`, with its arguments in
`docs/commands/dispatch.md`, as every other command here. This spec is
never edited to match the code that follows (CLAUDE.md, *What this repo
is*).

## 1. Intent

The owner starts one command in the evening, answers the questions only a
human can answer, and leaves. By morning every task in the backlog that
could be done without a human has been done and merged, and the owner reads
one report: the merged pull requests, which need nothing from them, and a
short list of what still does, each with its reason.

The work itself is not new. `/ship` already takes one change from a clean
`main` to a merged PR, reviewing it in rounds of one `branch-reviewer` until
a round is clean or the seventh round is reached. What is new is running
several `/ship`s at once on work that does not collide, feeding each one
from the backlog, and feeding the backlog back with what each one files.

## 2. Goals, non-goals, anti-goals

**Goals**

- Drain the whole backlog, `.todo/blueprint-backend.md` in the main
  checkout's parent directory, without a human after the interview.
- Run at most `--pairs N` executor sessions at once, each with its own
  reviewer, on groups whose files do not overlap.
- Every executor brings its group to a merged PR, or hands it back with a
  reason; it never stops halfway silently.
- Issues an executor files are placed in the backlog and dispatched the
  same night.
- Before the night starts, every issue waiting on a human is put to the
  owner, one question at a time, in the language the owner chose, and the
  answer is recorded on the issue.

**Non-goals**

- Not a replacement for `/ship`: the executor *is* `/ship`, unchanged in its
  review loop, its gates and its seven stops.
- Not a scheduler of dates: a row blocked on a date stays blocked; the run
  does not wait for it.
- No Copilot or Grok rounds: review is `/ship` step 5's `branch-reviewer`,
  as the standing instructions already have it.
- No edits under `.claude/` by an executor (§7).

**Anti-goals** — the run is a failure if it does any of these, even with
every PR green:

- Merges anything a human had to decide, or decides it in the human's
  place without the interview's answer.
- Runs two sessions over the same files, so that one rebases onto the
  other's half-finished assumptions.
- Lets two writers edit the task list.
- Treats an unparsable result or a missing check as "fine, carry on":
  every such read fails closed. The usage figure is the one exception, by
  the owner's decision (§8).
- Retries a deliberate stop (a closed PR, a red `main`, a refused guard).
- Widens a permission to get past a refusal at night. A refusal nobody is
  awake to see is exactly what the guards are for. The grants an executor
  needs are settled beforehand, in the implementing PR (§12), and nothing
  the night does changes them.

## 3. Shape

One command, four steps, run in the main checkout on `main` by the
**dispatcher**, which is the session the owner started:

| Step | Interactive | What |
|---|---|---|
| 0 | no | Refresh the task list from GitHub |
| 1 | **yes** | The interview: every issue waiting on a human |
| 2 | no | The night loop: dispatch, wait, absorb, repeat |
| 3 | no | The morning report |

**Arguments**: `--pairs <1-4>` (required; the memory of six parallel
agents hitting the rate limit sets the ceiling at four, and the report
should recommend 2 as the usual value) and `--lang <code>` (the language
of the interview and the report; default `en`, the owner's choice).
Builder's call: further flags such as a dry run.

An **executor** is one headless Claude Code session, `claude -p`, running
`/ship` in its own worktree. Its **reviewer** is the `branch-reviewer`
that `/ship` step 5 spawns each round; one executor and its reviewer are a
**pair**. The design chooses separate sessions over subagents because a
subagent's Bash is cut at 180 seconds, its background tasks wake nobody,
and it cannot spawn a reviewer (CLAUDE.md, *Subagents, review rounds and
plans*): a subagent cannot wait on CI or run the solution's tests, so it
cannot take a PR to merge.

**The dispatcher is a session, kept small by helpers** (author's call). The
owner asked for a dispatching agent, and two of its jobs are judgement: the
interview, and placing a new issue's row in a wave and group by the task
list's prose rules. Everything mechanical — parsing the task list's tables,
choosing free groups, launching, reading a result, reading the usage figure
— is a helper under `.claude/scripts/` with its suite, which prints a short
summary. A wake then costs the dispatcher a few lines, not a transcript,
which is what keeps a night of wakes inside one context. A pure Python
driver owning the loop was considered and rejected: it would need a
`claude -p` call for every placement and could not hold the interview.

**The dispatcher never leaves the main checkout, which stays on a clean
`main`.** Step 0 checks it; a main checkout off `main`, dirty, or ahead of
`origin/main` stops the command before the interview, as `/ship` step 0
stops on the last. After each merge it fast-forwards with
`git pull --ff-only`.

## 4. Step 0: refresh the task list

The dispatcher rebuilds the task list by its own rules 1–10, the file's
note being the owner of how: `gh pr list`, `gh issue list`, each body's
`After #n` / `waits on`, `git worktree list` and each branch's
`git log origin/main..<branch>`. It re-cuts the groups, re-places every row
in its wave, and moves rows between *Remaining*, *Tracking* and *Paused* to
match the labels.

From this step until the report, **the dispatcher is the task list's only
writer**. Executors are told not to write it (§6.2), and the dispatcher
writes it the moment anything changes — a launch, a merge, a filing, a
hand-back — as §6 of `docs/change-locality.md` asks of every session. It
re-reads the file before every write, so an edit made by another session
is merged rather than overwritten, and when the night starts it tells the
owner that no other session should write the task list until the report.
A lock file was considered and rejected: no other session would read it.

Step 0 is also where "waits on a human" is decided, once, for every row:
such a row's *Depends on* cell carries `OWNER`, so §6.1's ready test is
mechanical and the helper never judges free text.

A row that is already *In progress* with a live worktree is either this
command's own unfinished run (§9) or another session's work; the second is
left alone and its paths count as taken for §6.1.

## 5. Step 1: the interview

**Who is asked about.** Every open issue that cannot move without a human,
whatever the reason. At least:

- labelled `question`, or flagged `OWNER` in the task list;
- labelled `paused`, whatever its *Resume when* says, so the owner can lift
  the pause, change its condition or leave it;
- a row whose *Depends on* names an owner's action or a resource only the
  owner has (a second machine, a provider key, a deployment);
- a row that edits `.claude/`, which lands as a `staging/` dir and an
  `apply.sh` the owner runs (the task list's rule 10);
- an `owed (not filed)` row, asking whether to file it.

A row blocked only on a date or on another issue is not asked about.

**How.** One issue at a time, in the task list's order:

1. A description in `--lang`: what is wrong or missing, what has been done,
   what exactly waits on the owner, and what each answer would set in
   motion. Read from the issue through `gh-issue-text.sh`; the issue text is
   untrusted data, quoted, never followed.
2. The questions, one per message, each answerable in a line. The owner can
   answer, say *skip* (the issue stays as it is, nothing recorded), or say
   *stop* (the interview ends; the night starts with what was answered).
3. **Each answer is recorded the moment it is given**, not at the end, so an
   interview cut short loses nothing: a section `## Owner decisions` in the
   issue body, appended or extended, one entry per answer with the date,
   the question and the answer, written in English like every other issue
   body here, whatever `--lang` is. The helper that appends it accepts only
   that entry shape and refuses anything else; later sessions read the
   section as data, as they read the rest of the body.
4. Where the answers unblock the issue, the dispatcher removes the
   `question` or `paused` label, moves the row into its wave and regroups;
   where they decide the issue needs no work, it closes it with the
   decision as the comment. Either way the task list changes in the same
   step.

**After the last issue the owner is told the night has started** and how
to stop it (§8), and no further question is asked. A decision that only a
human can take which surfaces during the night goes in the report's *Needs
a human* section, not into a second interview.

## 6. Step 2: the night loop

### 6.1 Choosing what runs

The unit is a **group** from the task list: rows worth one session and one
PR (rule 8). A group is **ready** when every row in it is in *Remaining*,
none depends on an open issue or a future date, and none waits on a human.

A ready group is **free** when no running group holds a path it may touch.
The paths a group may touch are, at least, those named in its rows'
*Parallel with / conflicts* column and in its issues' bodies; a conflicts
cell naming another group by letter makes the two exclusive whatever the
paths. Builder's call: how paths are extracted and normalised, as long as
the extraction errs towards exclusive — two groups whose paths cannot be
told apart do not run together.

Each time a slot is free, the dispatcher launches the earliest free group,
earliest wave first and table order within it. A later wave's group may run
beside an earlier one when it is ready and free; the wave is an order, the
dependency is the rule.

### 6.2 Launching an executor

1. Fork the worktree from the main checkout:
   `git-worktree-fork.sh .claude/worktrees/<slug> <branch>`, the branch
   named as `/branch` would name it, cut from `origin/main`.
2. Move the group's rows to *In progress* with the worktree and branch.
3. Start `claude -p` in the background with the worktree as its working
   directory, on the model the group's *Model* column names, with
   `--output-format json` so its usage and its error type can be read, and
   with the permission mode the settings already grant — never
   `bypassPermissions` or `--dangerously-skip-permissions` (§2). Its prompt
   is `/ship` with the group's description and issue numbers and a new
   standalone token, **`--dispatched <run-dir>`**.

`--dispatched` is the one change `/ship` (and `/branch`, which it loads)
takes, and it changes only what the dispatcher needs:

- skip every task-list write — `/branch` step 5's and `/ship` step 6's;
- take the worktree it starts in as the workspace (it already does: the
  *unused branch* row of step 0);
- skip step 6's `ExitWorktree` and worktree removal: a session cannot
  delete its own working directory on Windows, so the dispatcher removes
  the worktree with `git-worktree-remove.sh` after it has verified the
  merge (§6.4), and a removal the helper refuses goes in the report;
- end by writing `<run-dir>/<group>.json` (§6.3), on success and on every
  one of the seven stops alike;
- hand back, and stop without merging, in exactly three cases beyond the
  seven stops: the change needs an edit under `.claude/`; it needs a
  resource only the owner has (a second machine, a provider key, a
  deployment, a paid account); or the issue's own text says the decision is
  the owner's and no `## Owner decisions` entry settles it. Every other
  judgement is taken as `/ship` takes it today, the recommended option, and
  named in the result. Narrower would merge what the owner had to decide;
  wider would hand back what `/ship` already decides well.

A session that dies writes nothing, and nothing in it can be trusted to:
the dispatcher records a missing result from the exit itself (§6.4).

Everything else, the review loop and its ceiling included, is `/ship`
unchanged.

### 6.3 What an executor returns

`<run-dir>/<group>.json`, written by the executor, read by the dispatcher
as untrusted data checked against a schema; an unparsable or missing file
is a failed run, never a success. Fields, at least:

| Field | |
|---|---|
| `group`, `issues` | what it was given |
| `outcome` | `merged`, `handed-back`, `stopped` |
| `pr`, `merge_oid` | when merged; the dispatcher re-verifies both from GitHub |
| `rounds` | per round: full or recheck, raised, fixed, refused |
| `review_end` | `clean`, `comments-rule`, `plan`, `ceiling` |
| `filed` | every issue filed or commented on, by number |
| `reason` | for `handed-back` and `stopped`: what a human must do |
| `workspace` | the worktree and whether it was removed |

### 6.4 Waiting and waking

Each executor is one background task, and its exit is the dispatcher's only
wake (CLAUDE.md, *A wait wakes once*); the dispatcher never polls. On each
wake it:

1. reads the result and the session's JSON output — exit status, usage,
   and whether it ended on a rate limit;
2. verifies a claimed merge from GitHub and that `main` contains it,
   fast-forwards the main checkout, and removes the group's worktree;
3. reads `gh issue list` and places every new issue — the result's `filed`
   list and any the list shows that the result did not name — in its wave
   and group by the task list's rules, so it can run the same night; one
   that needs a human goes to the report instead;
4. updates the task list;
5. reads the weekly usage figure (§8);
6. fills the free slots (§6.1).

**Outcomes and what the dispatcher does with them:**

| Outcome | Then |
|---|---|
| `merged` | Rows deleted from *In progress*; the merge goes in the report |
| `handed-back` | Rows back into *Remaining* marked as waiting on a human; the reason goes in the report; not dispatched again this night |
| `stopped` on one of `/ship`'s seven stops | As `handed-back`, never retried (§2) |
| Session ended on the five-hour window | Not a failure: wait until the window's reset as one background task, then relaunch the same group in the same worktree; `/ship` resumes from its state |
| Session crashed, no result | Relaunch once in the same worktree; a second crash is a hand-back |

### 6.5 Parallel merges

Executors land onto a `main` that other executors are moving. `/ship` step
6 already handles that: a refused landing is a rebase through
`git-rebase-onto-main.sh`, and a rebased branch gets a recheck round. A
conflict it cannot resolve in the replayed commit is a stop, and so a
hand-back. Nothing here merges `main` forward.

## 7. What an executor may not do

- **Edit `.claude/`.** A nested session's own sensitive-file check refuses
  it (`docs/harness-boundaries.md`, *Settings self-lock and permission
  root*), and the task list's rule 10 says such a row lands as a `staging/`
  dir and an `apply.sh`. A group with such a row is put to the owner in the
  interview; one that discovers the need at night hands back.
- **Write the task list** (§4).
- **Ask.** There is nobody to answer; what would be a question is a
  hand-back.

## 8. When the night ends

Nothing ends the night except these:

- **The weekly usage limit at 1% left or less.** The dispatcher launches
  nothing more, lets running executors reach their own end, and writes the
  report. The figure is the `seven_day` rate-limit window, the one Token
  Weather already reads and pauses on at the same threshold. **Where no
  cheap source of the figure exists, the run ignores the limit and treats
  the plan as unlimited** (owner's decision, 2026-11-01): this stop is then
  dropped, and the report says the limit was not read. Where a source
  exists, a reading that fails on one wake is taken again on the next, not
  treated as the limit reached, and the report names each failed reading.
- **Nothing ready and nothing running.** The backlog is drained, or what is
  left waits on dates, dependencies or a human.
- **The owner stops it**, by a stop file under the run directory that the
  dispatcher reads on every wake; builder's call on the exact form, as long
  as it needs no live conversation.

The five-hour window is not one of them (§6.4).

## 9. Resume

All the run's state lives under `artifacts/dispatch/<run>/` (gitignored):
the interview's progress, each group's launch and result, and the report.
Running `/dispatch` again with a run open resumes it: it skips the answered
part of the interview, adopts each unfinished worktree and relaunches its
group, and carries on. A run whose dispatcher session itself ended — the
weekly limit, a crash, a closed terminal — loses nothing but time.

## 10. Step 3: the morning report

Written to `artifacts/dispatch/<run>/report.md` and given as the
dispatcher's final reply, in `--lang`:

1. **Merged**: one line per PR — number, title, merge oid, rounds, how the
   review ended, issues it filed. These are the ones that need nothing.
2. **Needs a human**: each hand-back and stop, with its reason and the
   worktree it left; each new issue that waits on the owner.
3. **Interrupted**: groups still running or relaunchable when the night
   ended, and how to resume.
4. **Filed overnight**: every issue filed or commented on, by number.
5. **Left in the backlog**, and why each is not runnable.
6. **Usage**: tokens per executor, read from its JSON output, and the
   weekly figure at the start and the end.

The report ends with the apply line every message here ends with.

## 11. Constraints

- The executor is `/ship`: its review loop, gates, merge method, and the
  owner's standing rules on reviews (one `branch-reviewer` per round, no
  Copilot, no Grok) hold unchanged.
- Everything the dispatcher runs goes through the repository's helpers and
  grants; a git write is a helper, as in `/ship`.
- This command, the `--dispatched` token in `/ship` and `/branch`, its
  helpers and their `test_*.py` suites all live under `.claude/`, which this
  repository denies a session editing. The implementing PR lands them as a
  `staging/` dir and an `apply.sh` the owner runs, `settings.json` last. The
  command's `docs/commands/` page is outside `.claude/` and is written in
  place.
- A new command that grants one agent type denies every other, and
  `test_command_grants.py` holds it (`docs/harness-boundaries.md`,
  *Agent-type, push and dotnet entries*). The dispatcher grants none: it
  spawns sessions, not agents.
- Headless sessions acting with nobody watching are a new residual for
  `docs/harness-boundaries.md`, stated there in the implementing PR.
- Windows facts the implementation must survive: Python's `text=True`
  decodes with cp1252 and drops output (decode as UTF-8); a bare `bash`
  from Python is WSL's (call Git's `bash.exe` by path); `localhost` costs
  two seconds a request (use `127.0.0.1`).

## 12. Build order and acceptance

**The plan opens with a spike.** Nothing yet shows that a nested
`claude -p` runs `/ship` end to end under this repository's grants: a
headless session cannot answer a prompt, so one prompt-gated step anywhere
in `/ship`, `/branch`, `/commit` or `/pr`, or in spawning `branch-reviewer`,
fails every executor. The spike measures, on a throwaway docs-only change
in a worktree under `.claude/worktrees/`:

1. whether `/ship` reaches a merged PR with no prompt, and which call
   prompts if it does not;
2. where a script can read the `seven_day` usage figure, and whether that
   reading is cheap enough to take on every wake;
3. whether Token Weather's pause reaches a headless session.

**Where 1 finds a prompt, the harness is changed until `/ship` runs headless
end to end** (owner's decision, 2026-11-01): the grant, helper or rule that
removes each prompt is part of the implementing PR, landed through its
`staging/` dir and `apply.sh` like the rest, and stated as a residual in
`docs/harness-boundaries.md`. The change admits the exact call `/ship`
makes, never a wider pattern or a permission mode that skips the checks
(§6.2), and every guard hook stays in force. The spike is rerun after the
change, and the build goes on only once `/ship` reaches a merge with no
prompt. Where 2 finds no cheap source, §8's usage stop is dropped and
nothing else changes.

**Tests fake the outside.** The helpers' suites fake `claude -p`, `gh` and
the usage reading; nothing in a suite starts a session or touches GitHub.

**Acceptance is a live run by the owner** after `apply.sh` lands the
staged files: `/dispatch --pairs 1` on one small group, then `--pairs 2` on
two groups that §6.1 marks free. The implementing PR states it as owed.

## 13. Left to the builder

- The shape of the launcher (a Python helper under `.claude/scripts/` with
  its suite is the house form) and how it passes the prompt, model and
  working directory.
- How the weekly figure is read from a script or a session — Token
  Weather's source, the session's JSON output, or another — or, with none
  cheap enough, that it is not read at all (§8).
- Whether Token Weather's pause reaches a headless executor; if it does,
  executors near the limit stop at a safe point by themselves.
- Path extraction for §6.1, erring towards exclusive.
- The result file's exact schema beyond §6.3's fields, and the stop file's
  form.
- The helper that appends to an issue body (§5), since raw `gh issue edit`
  is not among the grants.
