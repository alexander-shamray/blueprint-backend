# Agent rules

The working rules every repository on this harness shares. Each target's
`CLAUDE.md` imports this file on a line of its own, so every session loads
it before the first prompt; it is managed, and changed only in
`blueprint-harness`'s `payload/`. This file owns the rules it states, but
for the Edit-not-Write rule and the settings self-lock, which
`docs/harness-boundaries.md` owns; the documents under *Where the detail
is* hold their reasons and detail and cite this file for the rule.

## Hard rules

- **`py -3.12`, not `python`**: CI pins 3.12 and the local default is
  newer.
- **File permission rules take `Edit(...)`, never `Write(...)`**: a
  `Write(path)` rule matches nothing and stops Claude Code from starting
  (`docs/harness-boundaries.md`, *Edit denies and agent self-protection*).
- **`.claude/settings.json` self-locks, not instantaneously**: a change to
  it lands complete and goes last, and a restore is verified by reading
  the file (`docs/harness-boundaries.md`, *Settings self-lock and
  permission root*).
- **A gate with a suite is tested and then run**: a green build says
  nothing about a gate the build does not run.
- **A gate that silently stops covering the newest surface is the
  most-repeated failure**; the only defence is a test whose subject is
  what the gate is looking at, not what it found.

## Working in a target

- **Uncommitted work in the tree belongs in the PR being worked on** —
  never revert it to clean the tree.
- **The task list is the path `.harness/config.json` names under
  `task_list`**, in the main checkout's parent directory, outside every
  checkout. It is updated the moment a session starts a branch, opens,
  merges or closes a PR, or files, closes or reopens an issue — from a
  worktree as well.
- **A managed file is changed in `blueprint-harness`'s `payload/`**, never
  in the target, `blueprint-harness`'s own rendered root included.
  Managed means named in `.harness/manifest.json`; the drift gate refuses
  a managed file that differs from the payload, and the next sync
  overwrites it.
- **A `local` path under `.claude/`, and all of `.harness/`, is
  Edit-denied as a managed one is**:
  editing it takes the owner's lift, and a deny lifted mid-session can
  still refuse until Claude Code is relaunched from the worktree
  (`docs/harness-boundaries.md`, *Settings self-lock and permission
  root*).

## Subagents, review rounds and plans

Every turn re-sends the whole context, so an agent costs what it reads
times the turns it takes, and a tool result stays in the parent for the
rest of the session (`docs/harness/token-rules.md`, *What every session
loads*).

- **Delegate with a brief.** A subagent is handed the files, diff or
  question it needs and returns a short structured answer — findings as
  `file:line`, not prose. `Explore` before `general-purpose` for a search;
  nothing is delegated that one `Grep` answers; a step's reads go in one
  message (`docs/harness/token-rules.md`, *Briefing a subagent*).
- **Choose the model by who catches its error.** A subagent runs on a
  cheaper model where the parent, a gate, a build or a test catches its
  error, and on the parent's, or the stronger model its profile pins,
  where an error is a silent miss (`docs/harness/token-rules.md`,
  *Which model a subagent runs on*).
- **The review loop** is `/ship` step 5's, and a PR reviewed outside
  `/ship` runs it too: rounds of one `branch-reviewer` each, read-only by
  its tool list, handed the diff under `artifacts/review/` and not the
  author's conclusions — never `general-purpose`, and never the session
  reviewing its own diff (`docs/harness/token-rules.md`, *Reviewers*).
- **Plans.** A change that fits one PR takes plan mode, not a plan file. A
  plan file cites code by path and symbol rather than pasting it and stays
  under 50 KB; research for it goes to `Explore` with a brief, and the
  plan-writing agent is handed the spec and the symbols the plan cites
  and writes it in one pass, without reading back what it has written.
  Small tasks run with `executing-plans`; under
  `subagent-driven-development`, each reviewer is handed its task's diff,
  not the tree.
- **A wait wakes once.** A session about to wait on CI, a background agent
  or a scheduled check-in runs it as one background task whose exit is
  the only wake. Never hand a wait to a subagent: its Bash is cut at 180
  seconds and its own background task wakes nobody
  (`docs/harness/token-rules.md`, *Waits*).
- **A comment sweep** hands each agent what `.github/comment-gate/` finds
  in its files with `--tree`, not whole suites to read.
- **One task per session**: `/clear` between tasks.

## Handing over to the owner

- **A deny that blocks wanted work is surfaced, never routed round.** A
  permission rule, a hook or a classifier refusal is the owner's control
  surface: no `Write` in place of a denied `Edit`, no shell redirection
  into a denied path, and no note in a PR body standing in for the ask.
- **Do everything else first, then hand the denied edit over as a
  script.** Write it to the scratchpad as a script that replaces exact
  anchored strings and checks every anchor before writing anything, so it
  applies all its edits or none and names the file on a miss; dry-run the
  anchors and say they matched. Name it without underscores.
- **Every command the owner runs is one line with its leading `!`**, so it
  pastes straight into the prompt and its output lands in the session. A
  pasted line loses `_` and `*`, so a command holding either goes in a
  checked script and the line is `! bash <script>` or
  `! py -3.12 <script>`. A `!` line runs in Git Bash, so a Windows path
  with backslashes loses them and the line dies on its first word with no
  output: give the script's path with forward slashes.
- **The script writes a receipt beside itself**, so a script that never ran
  can be told from one that ran and failed; read the receipt before
  reading the files.
- **After the owner runs it, verify by reading the files**, then commit
  and carry on; the commit body says the owner applied it from a script.

## Where the detail is

- `docs/harness/working-rules.md` — before a commit, a PR, a review round,
  a CI wait, or filing an issue; it holds the review bar.
- `docs/harness/token-rules.md` — before briefing a subagent or choosing
  its model; the reasons for *Subagents, review rounds and plans*.
- `docs/harness/pitfalls.md` — when a shell, git, machine or auto-mode
  step behaves unexpectedly, and before a scratchpad script runs a
  subprocess or a heredoc carries an issue body.
- `docs/harness-boundaries.md` — before touching anything under
  `.claude/`: what the harness grants and refuses.
- `docs/harness-local.md` — this target's own: its change contract, style
  guide and testing document, what `/ship` step 2 runs here, its
  residuals and its review examples.
