# The task list

The shape of the task list and the rules that place its rows. One helper,
`dispatch_board.py tasks`, owns both: it is the only code that writes the
list, and it refuses, before writing, a list `board` would refuse. No
session edits the list by hand (`.harness/agent-rules.md`, *Working in a
target*), and the list carries no note of its own rules: this file is that
note. Where the list lives is `.harness/agent-rules.md`'s; when each event
happens is `docs/harness/working-rules.md`'s, *Issues and the task list*.

## The shape

`tasks init` writes an empty list, or takes the text above an existing
list's first section off and puts its one-line preamble there. Below the
preamble come exactly four sections, in this order, each a table or the
bare word `none`:

| Section | Columns |
|---|---|
| *In progress* | `Group`, `#`, `What`, `Branch or PR`, `State` |
| *Remaining* | `Wave`, `Group`, `#`, `Task`, `Labels`, `Depends on`, `Model`, `Parallel with / conflicts` |
| *Tracking* | *Remaining*'s |
| *Paused* | `#`, `Task`, `Labels`, `Why paused`, `Resume when` |

- **The `#` cell** is an issue number, or `owed (not filed)` for work with
  no issue. Anything else, a dash included, is refused: a row that names
  no issue and is not owed has nothing GitHub can settle.
- **A row lives in one table only**, and the list holds no history: no
  note of what merged or closed, which git and the closed issues keep.
- **`Group`** is a letter and a number (`A1`). In *In progress* it is the
  group the row carried in *Remaining*, kept so a conflicts cell naming it
  still sees it, or `—` for work no group held. In *Tracking* it may be
  `—`; in *Remaining* every row has one.

## Where a row sits

The issue's live state decides the section, and the helper reads it from
GitHub rather than from the caller:

| State | Section |
|---|---|
| Open, labelled `paused` | *Paused*, unless a session has it *In progress* |
| Open, labelled `tracking` and not `paused` | *Tracking* |
| Open, neither | *Remaining* |
| Taken up by a session, its own branch or worktree named | *In progress*, until the work ends |
| Implemented by an open pull request | *In progress*: a row for each open, unpaused issue it closes, or one owed row when it closes none |
| Closed | No row, once *In progress* has ended its own |

An *In progress* row is the working session's, or a `/dispatch` run's: no
label and no close moves it, since another session's row is never the
helper's to drop, and it ends only through `tasks done`, or `tasks back` for
work that ends with its issue open, which `/ship` step 6 runs.
`tasks reconcile` names a stale one with the call that ends it.

## Placing a row in Remaining

- **Waves** order the table top to bottom in the order rows can be
  implemented, and a row never sits above one it depends on; within a
  wave, and in *Paused*, undated rows come first, then dated ones nearest
  first. Rows of one
  wave can run at once unless *Parallel with / conflicts* says they share
  files.
- **Groups** are rows worth one session and, where code or docs change,
  one pull request: they touch the same files or owner, or need the same
  background, so reading it once serves all. Rows of a group sit adjacent
  and stay at about three issues or one family. A group never spans a
  wave, a dependency or a model; a row that shares nothing with its
  neighbours is a group of one. A re-cut never reuses a letter and number
  an *In progress* row still carries.
- **`Depends on`** is `none`, issue references (`#n`, `after n`,
  `waits on n`), ISO dates, or `OWNER` for an owner's decision. An issue
  labelled `question` carries `OWNER`. Other free text holds the row: the
  board cannot judge it.
- **`Model`** is the session's: `Opus` where an error is a silent miss (a
  test that cannot fail, harness guards, locking, erasure, cross-cutting
  CI), `Sonnet` where a gate, a test or a mutation proof catches it.
- **Unplaced.** A row the helper adds with no wave and group the caller
  chose goes to the last wave under a fresh `U<k>` group with *Depends on*
  `unplaced: no session has chosen its wave and group`, which the board
  holds until a regrouping replaces it. Grouping is a judgement, so the
  helper never makes it, and of the rules above it refuses only a group
  spanning a wave or a model. The order is the regrouping's to keep: the
  helper puts a row it adds after its group or wave, and a *Paused* row
  last.

A row the helper adds takes the issue's title as its task only when the
repository owner opened the issue; any other reads `Issue #<n>, not opened
by the repository owner`, since the dispatcher reads the list and a
stranger's title is text it would read as the owner's.

*Paused* rows take *Why paused* and *Resume when* from the caller, or say
the label and its removal. A row leaving *Paused* returns unplaced: its old
wave and group were not kept.

## The events and their calls

Every event in `docs/harness/working-rules.md`'s list is one call. Each
takes no path and no run: the list is the one `.harness/config.json`
names. A write that would leave a list `board` refuses is refused whole;
each reads the file again before replacing it, and starts over when
another session changed it in between.

| Event | Call |
|---|---|
| Work starts on an issue | `tasks start <n> --branch <b> --worktree <w>` (`--what` when the issue has no row) |
| Work starts with no issue | `tasks start owed --branch <b> --worktree <w> --what <text>` |
| A pull request opens | `tasks state <n> 'PR #<p> open'`, or `state owed … --branch <b>` |
| A pull request merges | `tasks done <n>`, or `done owed --branch <b>` |
| Work ends with its issue open | `tasks back <n>` |
| An issue is filed | The filing helper calls `tasks place <n>`; a session placing it itself adds `--wave`, `--group` and the other cells |
| An issue is closed, reopened, paused or unpaused, or its `tracking` label changes | `tasks sync <n>`; `issue_decisions.py` runs it after `close` and `unblock` |
| Any doubt about the whole list | `tasks reconcile` |
| A regrouping | `tasks rewrite --from <file>` |

- **`tasks reconcile`** reads every issue through `gh-issue-list.sh` and
  each owed row's branch through `pr-for-branch.sh`, and applies every
  rule above at once outside *In progress*: rows of closed issues deleted,
  rows moved by label, and open issues with no row added unplaced. It names
  each change, and each row it leaves for a person: one naming no issue
  here; an *In progress* row whose issue closed or whose owed work merged,
  with the `done` that ends it; and an open issue whose pull request merged
  without closing it.
- **`tasks rewrite`** takes a whole list a session wrote and replaces the
  live one with it, refused unless *In progress* is unchanged and every
  issue's row is still in its section: a rewrite re-cuts waves, groups and
  cells, and adds, drops or rewords only owed rows. `/dispatch`'s own
  `rewrite --run` holds the same line.
- **A sync pull request gets no row.** It has no issue, `/dispatch` reads it
  from `sync_pr.py list`, and it lives for minutes; `/ship sync #<n>` writes
  nothing to the list.
- **A `/dispatch` executor writes no list.** The dispatcher sets
  `HARNESS_DISPATCHED` in each executor's environment, every run-less write
  refuses under it, and a filing helper files and places nothing; the
  dispatcher places what the result's `filed` names, through
  `tasks place --run`.
- **A filing that cannot place its row still files.** The helper says so on
  stderr with the call that repairs it and exits 0, since a non-zero exit
  would read as a failed filing and file it twice.
  `gh-issue-harness.sh` files in the harness from another repository, so it
  places nothing: the harness's next `tasks reconcile` adds the row.
