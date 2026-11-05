# Shared harness plan 4: `/dispatch` takes sync pull requests

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.
>
> **Execution method:** subagent-driven, as the owner chose for plans 1–3.

**Goal:** a sync pull request lands without the owner launching anything.
`/dispatch` finds it, proves it is what the harness released, and runs one
headless `/ship` on it. That `/ship` reviews only what is specific to the
target, in one round, and merges on green. `/ship` also stops leaving its
merged branch behind.

**Architecture:** a payload script, `sync_pr.py`, does the three things
nothing does today:
- it lists the open sync pull requests;
- it verifies one: every managed file on its head is the same blob as the
  payload at the harness commit its manifest names, and that commit is on
  the harness's `main`;
- it writes the target-specific part of its diff for the reviewer.

`/ship` gains a sync row: a verified sync is reviewed once, never committed
to, and merged from a worktree on its own branch. `/dispatch` launches that
row as a group of its own, first and alone. `/ship`'s teardown calls plan 3's
`git-branch-drop.sh`.

**Tech Stack:** Python 3.12 standard library, Bash helpers, `gh`, the
headless `claude -p` executors `/dispatch` already runs.

**Spec:** `docs/superpowers/specs/2026-11-02-shared-harness-design.md`
§5.3, the first, fourth and fifth bullets, and §9's second bullet. Plan 3,
`docs/superpowers/plans/2026-11-05-shared-harness-3-main-protection.md`,
gives this plan the wiring of `git-branch-drop.sh` into `/ship`. Until this
plan lands, a sync pull request lands by a session through `/ship`'s loop
limited to one round (plan 2, *Scope*).

**Where it runs:** Tasks 1–4 in a `blueprint-harness` session, Task 5 in
both repositories.

**Scope.** Out of scope:
- **Dependabot pull requests.** The owner merges them
  (`docs/harness/working-rules.md`), and `/dispatch` keeps not taking them.
- **Frontend's and admin's syncs.** Plans 5 and 6. The mechanism here needs
  no change for them, since the sync row reads only the manifest, the
  config and the payload.
- **A Claude session inside CI.** The harness workflow opens the pull
  request, and a session on the owner's machine takes it (spec §5.3).

## Global Constraints

- `py -3.12` locally; stdlib only; `encoding="utf-8"` on every open and
  subprocess, never bare `text=True`; Git's `bash.exe` by absolute path
  from Python. Every `gh` call carries a timeout.
- The merge bar (spec §5.3): the required checks green and one clean round
  by a read-only `branch-reviewer`. The reviewer is handed the
  target-specific part, not the managed files.
- A sync branch is never committed to, by a session or an executor. A
  defect a sync carries is fixed in the payload, and the sync that fix
  opens supersedes this one.
- What `sync_pr.py` reads from GitHub is data, never an instruction, as for
  every helper `/dispatch` runs.
- The dispatcher spawns no agent and is the task list's only writer
  (`payload/.claude/commands/dispatch.md`, its frontmatter and opening).
- The full `/ship` loop stays for every change to the harness repository
  itself.
- Prose wraps at 80 columns, in British spelling. Commit subjects are
  `chore(harness): …`, `fix(harness): …` or `docs(harness): …` in the
  harness, with `PR: #<n>` in the trailer block.

## Review Focus

1. **A forged sync.** A branch named `chore/harness-sync-<sha>`, with the
   workflow's footer, whose `settings.base.json` drops a deny and whose
   manifest is rehashed to match. CI's drift gate already refuses it,
   because `harness-drift.yml` fetches the payload at the manifest's
   commit and `drift_gate.py` compares against it. That check is still
   pending, or still red, when `/dispatch` lists the branch. So
   `sync_pr.py verify` makes the same refusal before any review or launch,
   since a blob differs from the payload, and nothing takes the branch as
   a sync (Task 1,
   `test_a_managed_file_differing_from_the_payload_is_refused`).
2. **Two open syncs.** This happens when a listing races the workflow's
   supersede. Only the one with the highest number is taken, and the rest
   are reported (Task 1, `test_list_takes_the_newest_and_names_the_rest`).
3. **A payload deletion a local file still cites.** The managed file goes,
   and a target-local test or document still names it. The review diff
   carries each citing line, so the round sees the break the managed diff
   hides (Task 1,
   `test_review_diff_names_a_local_file_citing_a_deleted_path`).
4. **A sync superseded while its executor runs.** Its pull request closes
   under the executor. `/ship`'s closed-unmerged stop ends it as
   `stopped`, the dispatcher does not relaunch it, and the reads after
   that exit list the new one (Task 4, `test_a_superseded_sync_is_not_relaunched`).
5. **A sync behind a moved `main`.** Step 6's rebase rewrites the sync's
   commit. The verify is run again on the rebased head before the merge,
   so a rebase cannot carry a changed managed file in (Task 2,
   `test_step_6_verifies_again_after_a_rebase`).

---

## File structure

| Path | Where | Responsibility |
|---|---|---|
| `payload/.claude/scripts/sync_pr.py` | harness | `list`, `verify`, `review-diff`, `local-suites` |
| `payload/.claude/scripts/test_sync_pr.py` | harness | Its suite, over a fake `gh` and real repositories |
| `harness_sync/tests/test_sync_pr_constants.py` | harness | The payload script's sync shape equals `harness_sync/pr.py`'s |
| `payload/.claude/scripts/git-worktree-sync.sh` | harness | A worktree on an existing sync branch, tracking `origin` |
| `payload/.claude/scripts/test_git_worktree_sync.py` | harness | Its suite |
| `payload/.claude/commands/ship.md`, `payload/docs/commands/ship.md` | harness | The sync row; the branch drop in the teardown |
| `payload/.claude/scripts/test_ship_sync.py` | harness | Pins the sync row's text and the teardown order |
| `payload/.claude/commands/dispatch.md`, `payload/docs/commands/dispatch.md` | harness | Sync groups: found, launched first and alone, reported |
| `payload/.claude/scripts/dispatch_run.py` | harness | `sync-launch`, `sync-close`, `sync_prompt`, the report's *Syncs* section |
| `payload/.claude/scripts/test_dispatch_sync.py` | harness | Its new cases, in a payload module of their own |

The new dispatch cases go in a module of their own because
`test_dispatch_run.py` is backend-local (`targets/blueprint-backend.config.json`
`local`), and issue #5 is splitting it now *(builder's call)*.

---

### Task 1: Identify, verify and diff a sync pull request

**Files:**
- Create: `payload/.claude/scripts/sync_pr.py`, its suite, and
  `harness_sync/tests/test_sync_pr_constants.py`
- Modify:
  - `payload/.harness/settings.base.json`: `allow` gains
    `Bash(py -3.12 .claude/scripts/sync_pr.py:*)`;
  - `payload/docs/harness-boundaries.md`: *Grant inventory and push
    helpers* gains the script's row;
  - `harness_sync/pr.py` `pr_body`: the line saying "the review here is of
    what is specific to this target" names how it is taken, by `/dispatch`
    or by `/ship` on this branch, and `sync_pr.py review-diff` as what the
    round reads.

**Interfaces:**
- Produces:
  - `HARNESS_REPO = "alexander-shamray/blueprint-harness"`,
    `SYNC_HEAD = re.compile(r"chore/harness-sync-[0-9a-f]{7}")` and
    `FOOTER`, equal to `harness_sync/pr.py`'s.
  - `py -3.12 .claude/scripts/sync_pr.py list`. It prints JSON,
    `{"take": <row or null>, "rest": [<row>…]}`, where a row is
    `{"number", "head", "oid"}`. A row is an open, same-repository pull
    request into `main` whose head matches `SYNC_HEAD`. Its body's last
    line must be `FOOTER` followed by `.`, or by `, in run <url>.`, which
    are the two shapes `pr_body` writes. `take` is the highest number.
    Exit 0.
  - `sync_pr.py verify <n>`. It exits 0 with `verified <n> <harness sha>`,
    or 1 with one line per failure. Exit 2 is a refusal with nothing read,
    for example a bad argument.
  - `sync_pr.py review-diff <n> <out>`. It writes the reviewer's input
    and prints its path.
  - `sync_pr.py local-suites`. It prints the target-local `test_*.py`
    paths that `.harness/config.json` `local` matches, one per line.
  - Every `gh` and `git` call goes through one runner that the tests swap
    for a fake, as `test_pr.py`'s `FakeGh` does.

**What `verify` checks**, all of it, reading the pull request's head after
`git fetch origin <head>`:
1. The pull request is a `list` row.
2. The head's `.harness/manifest.json` `harness_commit` is on the harness's
   `main`: `gh api repos/<HARNESS_REPO>/compare/main...<sha>` reads
   `behind` or `identical`.
3. Every manifest path except `.claude/settings.json` has the same blob id
   on the head, `git ls-tree -r <head>`, as `payload/<path>` at that
   commit. The commit's tree comes from one call,
   `gh api repos/<HARNESS_REPO>/git/trees/<sha>?recursive=1`. On
   2026-11-05 all 122 of backend's such paths matched by blob id.
4. The paths the head changes against `origin/main` are manifest paths,
   paths deleted from the old manifest, and `.harness/manifest.json`,
   and nothing else. `.harness/config.json` is untouched.

`.claude/settings.json` is left to the drift gate, which renders it again
from the verified base and the unchanged config *(builder's call)*.

Checks 2 and 3 are what CI's drift gate does from its fetched payload. The
verify repeats them on the owner's machine, so that a sync is proven
before its review and its launch, not only before its merge.

**What `review-diff` writes**, under `artifacts/review/<slug>-r1.diff`, the
path convention `/ship` step 5 already uses:
- the diff of `.claude/settings.json`;
- the old and new harness commits, with the subjects between them, from
  `compare`;
- the managed paths written and deleted, by name only;
- for each deleted path, every line of a tracked file outside the manifest
  that names it, as `file:line: text`.

No managed file's content is in it.

- [ ] **Step 1: Tests**, in `test_sync_pr.py`:
  - `test_an_authentic_sync_verifies`;
  - `test_a_branch_name_alone_is_not_a_sync`, a body without the footer;
  - `test_a_cross_repository_head_is_refused`;
  - `test_a_managed_file_differing_from_the_payload_is_refused`, the forged
    sync of Review Focus 1;
  - `test_a_harness_commit_off_main_is_refused`;
  - `test_a_path_outside_the_manifest_is_refused`;
  - `test_a_config_edit_is_refused`;
  - `test_settings_json_is_not_compared_with_the_payload`;
  - `test_list_takes_the_newest_and_names_the_rest`;
  - `test_review_diff_names_a_local_file_citing_a_deleted_path`;
  - `test_review_diff_holds_no_managed_file_content`;
  - `test_local_suites_reads_the_config`.

  In `test_sync_pr_constants.py`, which runs in the harness only:
  `test_the_payload_reads_the_sync_shape_pr_py_writes`. It checks the
  branch pattern, and that the footer line accepted is one of the two
  `pr_body` writes, with and without a run URL.
- [ ] **Step 2:** Run the tests and see them fail. Write the script, the
  grant and the prose, then run them and see them pass. Run
  `payload-suites` as CI does, on the rendered root.
- [ ] **Step 3:** Commit, open the PR, run the `/ship` loop, and merge.

### Task 2: `/ship` takes a sync pull request

**Files:**
- Create: `payload/.claude/scripts/git-worktree-sync.sh`, its suite, and
  `payload/.claude/scripts/test_ship_sync.py`
- Modify:
  - `payload/.claude/commands/ship.md`: a new section, *A sync pull
    request is reviewed once, for this target*, after *A plan is reviewed
    once, for contradiction*, and its row in *Resume, don't restart*;
  - `payload/docs/commands/ship.md`: the section's argument;
  - `payload/docs/harness/working-rules.md`: *A sync pull request is merged
    from its own branch* now cites `/ship`'s section rather than spelling
    out the worktree;
  - `payload/.harness/settings.base.json`: `allow` gains
    `Bash(bash .claude/scripts/git-worktree-sync.sh:*)`;
  - `payload/docs/harness-boundaries.md`: the helper's row.

**Interfaces:**
- Consumes: Task 1's `verify`, `review-diff` and `local-suites`.
- Produces:
  - `git-worktree-sync.sh <path> <branch>`. It runs
    `git fetch origin <branch>` and then
    `git worktree add --track -b <branch> <path> origin/<branch>`. Its
    refusals:
    - the branch does not match `SYNC_HEAD`, `main` included;
    - the path is not `.claude/worktrees/<name>`;
    - it is not run from the main checkout's root;
    - the branch already exists locally;
    - `origin` lacks the branch.

    Its path and root checks are `git-worktree-fork.sh`'s.
  - The `/ship` argument `sync #<n>`. From the main checkout on `main`,
    step 1 runs `git-worktree-sync.sh .claude/worktrees/sync-<sha7>
    <head>` in place of `/branch`, and enters it.
  - Under `--dispatched`, a `sync #<n>` run names its group `sync-<n>`. It
    writes `<run-dir>/sync-<n>.json` with `"group": "sync-<n>"` and
    `"issues": []`. The argument carries no `#<group>-<n>` tag, so the
    Report's rule for reading the group gains this case.

**The sync row** (the section states it; `docs/commands/ship.md` argues
it):
- **What makes a branch a sync.** It matches `SYNC_HEAD`, and
  `sync_pr.py verify <n>` exits 0. A branch that matches `SYNC_HEAD` but
  fails the verify is not reviewed at all:
  - under `--dispatched` it is handed back, with the verify's lines as the
    reason;
  - otherwise the run reports them and ends unmerged. This is not one of
    the seven stops, because nothing ran that could have failed.
- **Step 2** runs each path `sync_pr.py local-suites` prints as
  `py -3.12 -m unittest discover -s <dir> -p <file>`. That is how CI runs
  them, since they import siblings from their own directory, and a dotted
  module name cannot be formed from `.claude/` or `.github/comment-gate/`.
  These are the target-local tests a payload change can trip, which the
  harness never runs (`docs/harness/working-rules.md`). Nothing else under
  *Ship checks* runs, since the managed files were checked in the harness.
- **Steps 3 and 4** do nothing: the tree stays clean and nothing is
  committed or pushed. A dirty tree on a sync branch is the sync row's own
  case, not the open-PR row's: report what the tree holds and end
  unmerged, committing nothing.
- **Step 5** is one round, handed `review-diff`'s file and not the branch
  diff.
  - A finding at `bug` or `medium` is filed in the repository whose code it
    is about, citing the sync pull request. The sync is then handed back
    under `--dispatched`, or ends unmerged otherwise.
    - When it is about the payload, the fix's own sync supersedes this
      one.
    - When it is about a target-local file, for example Review Focus 3's
      local line citing a deleted path, it is filed in the target. This
      same sync is taken again once that fix has merged.
  - A `low` finding goes into one harness issue, `Low findings left by
    <repo>#<n>: <title>`, and the merge goes on.
  - A `nit` is not filed.
  - So a round is clean when no `bug` or `medium` finding is open
    *(builder's call: the user-level filing rule already sends lows to an
    issue, and a hand-back for a low is a stop that §4 counts)*.
- **Step 6** is unchanged, except after a rebase. When its rebase helper
  ran, `sync_pr.py verify <n>` runs again on the new head before
  `gh-pr-merge.sh`, and that verify replaces the step 5 recheck which
  step 6 otherwise re-enters. A sync therefore keeps its one round. A
  failed verify is handed back.

- [ ] **Step 1: Tests.**
  - `test_git_worktree_sync.py`:
    - `test_only_a_sync_head_is_accepted`;
    - `test_main_is_refused_however_it_is_spelled`;
    - `test_an_existing_local_branch_is_refused`;
    - `test_the_worktree_tracks_the_origin_tip`;
    - `test_it_runs_only_from_the_main_checkout_root`.

    All are over a bare origin, as `test_git_worktree_fork.py` does.
  - `test_ship_sync.py`, which reads `ship.md` as
    `test_ship_dispatched.py` does:
    - `test_the_resume_table_has_a_sync_row`;
    - `test_a_sync_branch_is_never_committed_to`, so steps 3 and 4 name no
      commit;
    - `test_the_sync_round_reads_review_diff`;
    - `test_step_6_verifies_again_after_a_rebase`, which also checks that
      no recheck round follows;
    - `test_a_failed_verify_reviews_nothing`;
    - `test_a_dispatched_sync_names_its_group`.
- [ ] **Step 2:** Run the tests and see them fail. Write the helper and the
  text, then run them and see them pass. Run `payload-suites` on the
  rendered root, and `test_ship_dispatched.py` and
  `test_ship_landing_method.py` with them.
- [ ] **Step 3:** Commit, open the PR, run the `/ship` loop, and merge.

### Task 3: `/ship` drops its merged branch

Plan 3's Task 5 must have merged, since this task calls its
`git-branch-drop.sh`.

**Files:**
- Modify:
  - `payload/.claude/commands/ship.md`:
    - step 0's teardown, where "Never delete the merged **branch**:
      `git branch -d` is denied; name it in the report and leave it." gives
      way to the drop;
    - step 6, where "Never delete the merged branch: `git branch -d` is
      denied; name it in the report." gives way to it too;
    - the report, where "and the merged branch still sitting in
      `git branch`" goes;
    - the stops table's first row, which gains the drop as a second
      reported exception beside `system-sleep.sh`;
  - `payload/docs/commands/ship.md`, which argues each of these;
  - `payload/.claude/commands/dispatch.md`, *On an executor's exit*: after
    a `merged-claimed` group's worktree is removed, the dispatcher runs the
    drop for its branch. Its frontmatter `allowed-tools` gains
    `Bash(bash .claude/scripts/git-branch-drop.sh:*)`.
- Test: `payload/.claude/scripts/test_ship_sync.py`

**Interfaces:**
- Consumes: `git-branch-drop.sh <branch>` (plan 3, Task 5).

The drop runs once the branch's worktree is gone:
- after `git-worktree-remove.sh` exits 0;
- or, for an in-place branch, after the switch to `main`.

A refusal is reported and is not a stop. The helper refuses only a branch
that has not landed, which is exactly the case where keeping it is right
*(builder's call)*. The raw `git branch -d` and `-D` denies stay.

- [ ] **Step 1: Tests**, in `test_ship_sync.py`:
  - `test_the_teardown_drops_the_branch_after_removing_its_worktree`, by
    order in both step 0 and step 6;
  - `test_no_passage_leaves_the_merged_branch`;
  - `test_a_refused_drop_is_not_a_stop`;
  - `test_dispatch_drops_after_its_removal`.
- [ ] **Step 2:** Run the tests and see them fail. Edit the text, then run
  them and see them pass, together with `test_ship_dispatched.py`.
- [ ] **Step 3:** Commit, open the PR, run the `/ship` loop, and merge.

### Task 4: `/dispatch` takes sync pull requests

**Files:**
- Modify:
  - `payload/.claude/commands/dispatch.md`:
    - step 0.3 also runs `sync_pr.py list`, and `verify` on its `take`;
    - step 0.4's board names the sync group;
    - step 1 asks nothing about it;
    - step 2 launches it first and alone;
    - step 2's reads after an executor's exit gain `sync_pr.py list`,
      and `verify` on its `take`, beside `gh-issue-list.sh`;
    - *On an executor's exit* skips both `tasks done` and `tasks back` for
      a sync group, and runs `sync-close` instead;
    - step 3's report gains *Syncs*;
    - the frontmatter `allowed-tools` gains the `sync_pr.py` and
      `git-worktree-sync.sh` grants. The new subcommands need none, since
      `dispatch_run.py`'s commands are reached through
      `dispatch_board.py`'s main, under the grant that already exists;
  - `payload/docs/commands/dispatch.md`: the argument;
  - `payload/.claude/scripts/dispatch_run.py`.
- Create: `payload/.claude/scripts/test_dispatch_sync.py`

**Interfaces:**
- Consumes: Task 1's `list` and `verify`; Task 2's `sync #<n>` argument and
  `git-worktree-sync.sh`.
- Produces in `dispatch_run.py`:
  - `sync_prompt(pr: int, run: str) -> str`, which returns
    `/ship sync #<pr> --dispatched artifacts/dispatch/<run>`;
  - the subcommand `dispatch_board.py sync-launch <run> <pr> --branch
    <head> --worktree <path>`. It records the group `sync-<pr>` in
    `state.json` with `kind: "sync"`, `issues: []` and model `Sonnet`,
    the `MODELS` key `launch` stores. It reads and writes no task list,
    and its worktree check is `launch`'s;
  - `dispatch_board.py run <run> sync-<pr>`, which takes no
    `--description` for a sync group and builds `sync_prompt`;
  - `dispatch_board.py sync-close <run> sync-<pr>`, run after `verify`
    and the teardown on a merge, or after the result is read on any other
    outcome. It sets the group's status to `closed` and keeps its outcome,
    so `report` never lists a merged sync as "not confirmed in main";
  - `report`, whose *Syncs* section lists each closed sync group with its
    outcome, pull request and merge oid.

The rules *(builder's calls)*:
- **First and alone.** A sync changes `.claude/` for every group after it.
  So while a sync group runs, no other group is launched, and a group
  launched after its merge forks from the new `main` and runs under the
  new settings. This costs one executor's time, about 15 minutes, once a
  night.
- **The model is Sonnet.** The verify, CI and the `branch-reviewer` catch
  its errors, and the reviewer runs on its own profile's model
  (`docs/harness/token-rules.md`, *Which model a subagent runs on*).
- **No task-list row.** No row ever held a sync, and the next sync
  supersedes it. The report carries it.
- **Each executor's exit lists again**, through step 2's reads. A sync
  whose executor ended `stopped` because its pull request closed is not
  relaunched. A new `take` after it is a new group.

- [ ] **Step 1: Tests**, in `test_dispatch_sync.py`:
  - `test_sync_prompt_reads_back_as_ships_sync_argument`;
  - `test_sync_launch_writes_no_task_list`;
  - `test_no_group_launches_while_a_sync_runs`;
  - `test_a_superseded_sync_is_not_relaunched`;
  - `test_a_sync_group_takes_no_description`;
  - `test_the_report_lists_syncs`;
  - `test_a_closed_sync_is_not_reported_unconfirmed`;
  - `test_a_stopped_sync_runs_no_tasks_back`;
  - `test_the_sync_result_validates_with_no_issues`, against
    `RESULT_KEYS` and `_result_problem`.
- [ ] **Step 2:** Run the tests and see them fail. Implement, then run them
  and see them pass, on the rendered root.
- [ ] **Step 3:** Backend holds `test_dispatch_run.py` and
  `test_dispatch_board.py` locally. Run them against the changed
  `dispatch_run.py` from a backend worktree that has the payload copy laid
  over its own. If one fails because it pins the old behaviour, change that
  local test in a backend pull request that merges before this task's sync
  opens. The sync cannot carry a local edit. Backend's CI runs that test
  against the old managed `dispatch_run.py`, so the changed test must pass
  against both copies, and it is run on both before that pull request
  opens. Name that pull request in this one's body.
- [ ] **Step 4:** Commit, open the PR, run the `/ship` loop, and merge.

### Task 5: Take it live, then let `/dispatch` take a sync

- [ ] **Step 1:** Land the harness self-sync and the backend sync that
  Task 4's merge opened, the old way: a session's `/ship` loop limited to
  one round, as plan 2 did. A target has no sync row until the sync
  carrying it has merged. The syncs that Tasks 1–3 opened have been
  superseded by then. Relaunch a session in each repository afterwards,
  since permissions cache at start (spec §6).
- [ ] **Step 2:** Make one small harness change, a pull request of its own
  on the `/ship` loop. Take the sync it opens in the harness with
  `/ship sync #<n>`, which is the first use of the Task 2 row. Record the
  verify line in the H-row of the harness task list.
- [ ] **Step 3:** Leave Step 2's backend sync open for the night's
  `/dispatch`. Check the morning report's *Syncs* section
  for:
  - outcome `merged`;
  - one round;
  - the sync branch gone from `git branch`;
  - `main` green.

  If it is not, file it in the harness as `high`.
- [ ] **Step 4:** Add *After plan 4* to the harness's
  `docs/baseline-2026-11.md`: the backend stop count from `measure/stops.py
  --command ship` over the sessions since Step 2, measured as *After plan
  2* was, and the number of syncs merged with no owner turn. A sync that
  needed the owner is a harness defect, filed there.
