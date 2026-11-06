# Shared harness 5: frontend's first sync — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** `blueprint-frontend` runs on the shared harness: every fix only its
copy holds is in the payload or recorded as refused, its stack rules live in
its config, and its first sync has merged with every required check green.

**Architecture:** Spec §7 step 3. The harness work comes first: an audit of
the diverged files, the ports it calls for, the §8 mechanisms, and sweep rows
each target owns. Then a rehearsal on a scratch copy. Then one frontend pull
request holds the config, the local files, the deletions and the apply
together, the shape #1773 proved on backend, because a drift gate refuses any
of them alone.

**Tech Stack:** Python 3.12, bash, `harness_sync`, GitHub rulesets. Frontend's
stack (Angular, npm, Capacitor) stays frontend's.

**Spec:** `docs/superpowers/specs/2026-11-02-shared-harness-design.md`, §5.2,
§7 step 3, §8. Plans 1–4 are done or live; this plan's harness tasks start
from harness `main` at `d7a7eed` or later, and frontend is pinned at
`41a3f45` for the audit.

## The state this starts from

Read on 2026-11-06, frontend `41a3f45` against harness `d7a7eed`:

| | Count |
|---|---|
| Payload files | 149 |
| Equal in frontend, LF-normalised | 1 |
| Present in both, differing | 67 |
| Absent from frontend | 81 |
| Tracked by frontend in the harness surface, absent from the payload | 27 |
| `settings.json` denies frontend has and the base lacks | 31 |
| Frontend auto-memories, `MEMORY.md` aside | 23 |

The rest of what this plan depends on:

- `.gitignore` already ignores `.claude/worktrees/`.
- Frontend's `main` has no `.github/closure-gate/closure_gate.py`, so README's
  *sibling lands its closure gate first* rule applies.
- `sync.yml` skips a target with no `.harness/config.json`.
- The harness's `rulesets/main.json` already holds frontend's eleven checks.
- The payload's `pr-locality.sh` refuses without
  `.github/locality-gate/classes.yml`, and frontend has a change contract but
  no such file.

## Global Constraints

- "Every managed file is common; a repository differs only through its own
  setting, never by replacing a managed file." (spec §5.2)
- Additions "may add allows, denies, hooks and MCP servers" but "never remove
  or narrow a base deny or hook". (spec §5.2)
- "Each repository's `main` stays green at every step of the rollout."
  (spec §6)
- "A target's own `.claude/` is frozen from the moment its first sync is
  opened until it merges." Harness branches open in frontend land first or are
  re-made against the payload. (spec §7)
- Security, in the owner's words: "главное не портить мэйн", and the plan
  fails "если сломается main или агенты станут чаще спрашивать".
- A guard behaviour is ported only if it passes the three criteria of
  `docs/harness-boundaries.md`, *The guards' threat model*. A spelling built
  only to hide a command from a guard is recorded as out of scope and not
  ported.
- A managed file is changed in `payload/`, never in frontend.
- In a frontend session `.claude/**` and `.github/**` are denied. Every edit
  there goes in an owner-run script under agent-rules *Handing over to the
  owner*, never round the deny.
- Merge bar: the `/ship` loop for harness and frontend pull requests. The first
  sync takes one review round, on the target-specific part.

## Decisions on the §8 mechanisms

Spec §8 asks the plan to record each one. Read on 2026-11-06 at the pins
above.

| Frontend mechanism | Decision | Why, and what moves |
|---|---|---|
| `hooks/index-refresh.py`, `test_index_refresh.py` | Drop the files; port five behaviours to `refresh-index.py` | The payload's hook is better fenced. It lacks compaction (`:1112`), the `secsweep-` refusal (`:314`), the forged-worktree check (`:227`), `index_openable` (`:460`), and seeding only where git ignores the index (`:528`) |
| `hooks/run-guard.sh`, `run-guard.py` | Payload, for the four guards alone | `render_settings` only adds hooks, so local cannot work. Without it a missing interpreter means exit 127, which does not block, so the guard does not run. The hints keep `hint-bound.sh` and the index hooks keep `runpy`, as the boundaries argue. A machine with no Git Bash has no `sh` and is the residual |
| `git-fetch-origin.sh`, `git-pull-ff-only.sh`, `test_git_transport_helpers.py` | Drop; the npm-deny class at `:222` stays local | Spec: "payload if backend lacks an equivalent". The payload has one: the grants and `guard-git-argv.py`. The helper also refuses the `main:main` fetch `ship.md` runs. Cluster A judges any tail flag the guard misses |
| `run-harness.py`, `in_process_hook.py`, `test_harness_helpers.py` | Drop; the npm cases (`:2961-3000`) stay local | Speed only, and `run-harness.py`'s `ALONE` list goes stale once the payload lands. Cluster A and B judge the cases case by case |
| `skills/codebase-index/scripts/gate-index.py`, `gate.sh`, `test_index_gate.py` | Payload, called from `cbx` | A branch-tracked journal is replayed on open (frontend #138), and `cbx` has no gate |
| `mcp-launch.mjs`, `test_mcp_launch.py` | Local | It assumes Node, and `.mcp.json` is outside the managed directories |
| `run-index` | Drop | `cbx` is stricter; its gate call moves to `cbx` |
| `.skill_version` | Drop, untracked and ignored | An installer artefact nothing reads; tracked, the drift gate refuses it |
| `gh-issue-filing.sh`, `gh-sweep-issue-create.sh` | Drop; cluster C judges the fixed route | The payload's `gh-issue-create.sh` has more routes, but its route is a free argument where frontend's is fixed |
| `gh-pr-create.sh` | Payload | `pr.md` runs a raw `gh pr create`. The helper pins repository, head and base and refuses a tracked or linked input |
| `npm-checks.sh` | Local, granted through config | It is the stack. *Ship checks* names it |
| `test_root_ignore_coverage.py` | Local | It reads frontend's generated trees; the payload's case covers a bare `*` only |
| `commands/review-branch.md`, `docs/commands/review-branch.md` | Local | As backend |

Two builder's calls on the settings:

- **`Bash(gh pr merge:*)` and `Bash(git worktree remove:*)` go into the base
  rather than being dropped.** Spec §5.2 calls the first a contradiction of
  `gh-pr-merge.sh` and marks dropping it as the builder's call. It is not a
  contradiction: a deny judges the typed command, never the commands inside a
  script (frontend `docs/harness-boundaries.md:805-810`). The raw forms are
  what `--admin` and `-f` come through, so every target should refuse them.
- **The other 29 frontend denies stay config additions**, `Bash(git
  *--no-index*)` and `Edit(.github/**)` among them.

## Review Focus

1. A ported guard refusal stops a command agents type every day: the plan's
   anti-goal. Task 2's replay reads real transcripts and any new refusal stops
   the port.
2. A frontend-only file under a managed directory is in neither the manifest
   nor `local`: the drift gate is red on the first sync. Task 5's rehearsal
   and Task 7's check list every such path first.
3. A frontend session open when the first sync merges still holds the old
   settings, which call `run-guard.sh index-refresh.py`, a hook the new closed
   set refuses. That is `PostToolUse` and `SessionStart`, so an error is
   reported after every call and nothing is blocked; the hints end `|| :`.
   Task 7 relaunches.
4. Backend or the harness has no sweep rows on the day a sync brings the new
   `sweep-slices.sh`: each sweep there refuses. Task 4 lands the row files
   first.
5. Frontend changes its harness after the pin, and the audit misses it. Task 7
   re-reads the range before opening.

---

### Task 1: Audit the diverged files

Harness session. Spec §7 step 3: "one audit per file cluster, by read-only
subagents, returning `file:line` and the fix".

**Files:**
- Modify (harness): `IMPORT.md`, a new section *Frontend audit, pinned at
  41a3f45*

Clusters:

| | Cluster | Frontend paths |
|---|---|---|
| A | Guards | `hooks/guard-git-argv.py`, `guard-edit-target.py`, `test_edit_target_guard.py`, the guard cases in `test_harness_helpers.py` |
| B | Git helpers | `git-*.sh`, their suites |
| C | GitHub helpers | `gh-*.sh`, `pr-*.sh`, `sweep-mark.sh`, `sweep-slices.sh`, `test_git_pr_trailer_helpers.py`, `test_sweep_slices.py` |
| D | Index | `hooks/index-*.py`, `skills/codebase-index/**`, their suites |
| E | Commands | `commands/*.md`, `agents/*.md`, `docs/commands/*.md`, `test_command_runbooks.py` |
| F | Boundaries | `docs/harness-boundaries.md` |
| G | Gates | `.github/commit-gate/`, `issue-form-gate/`, `ISSUE_TEMPLATE/finding.yml`, `workflows/commit-gate.yml` |

- [ ] **Step 1:** Spawn one read-only agent per cluster, with the two trees at
  their pins and this plan's *Decisions*. Each returns rows of: frontend
  `file:line`, the behaviour, whether the payload has it (`file:line`), a
  verdict and its one-line reason. Cluster A starts from the 85 functions only
  frontend's git guard has and the 11 only its edit guard has, and the 6 only
  the payload's git guard has.
- [ ] **Step 2:** Verdicts are closed:
  - `port`: it changes an outcome, and the payload lacks it;
  - `covered`: the payload does it, cited;
  - `out of scope`: a guard spelling outside the threat model;
  - `local`: it holds a stack or tree fact of frontend's;
  - `obsolete`: its reason is gone, cited.
  Cluster F also has `residual`: the paragraph goes to frontend's
  `docs/harness-local.md` *Residuals*.
- [ ] **Step 3:** Check each cluster A `port` row against the three criteria
  of *The guards' threat model* by hand, and each `covered` row by reading the
  cited line. An agent's verdict is not the record.
- [ ] **Step 4:** Write the rows to `IMPORT.md` and `/ship` that alone. The
  count of `port` rows per cluster goes in the PR body. Task 2 takes its work
  from this section.

### Task 2: Port what the audit found

Harness session. One `/ship` pull request per cluster holding a `port` row.
The order is G, B, C, D, E, A, so the riskiest cluster lands last, on the
others' tests.

**Files:**
- Modify (harness): `payload/` files by cluster
- Create (harness): `measure/replay_guard.py`, `measure/test_replay_guard.py`

**Interfaces:**
- Produces: `py -3.12 measure/replay_guard.py --project <glob> --since <date>
  --guard <hook path>`. It prints one JSON line per Bash command the guard
  refuses from the sessions' transcripts, and a final
  `{"commands": n, "refused": n}`. It imports nothing outside the standard
  library and reuses `measure/stops.py`'s transcript walk.

- [ ] **Step 1:** Each `port` row takes frontend's own test as its failing
  case, adapted to the payload's suite. Confirm it fails against the payload,
  then port, then confirm it passes. A row with no frontend test gets one.
- [ ] **Step 2:** Mark each row `ported` with the commit in `IMPORT.md`, in
  the same pull request.
- [ ] **Step 3:** For cluster A write the replay, with cases:
  - `test_a_refused_command_is_printed_with_its_session`;
  - `test_a_transcript_with_no_bash_prints_zero_commands`;
  - `test_an_unreadable_guard_exits_nonzero`, since a replay that cannot run
    must not read as no refusals.
- [ ] **Step 4:** Run the replay with the payload guard before and after the
  port, over `C--dev-ashamray-blueprint-backend*` since 2026-10-19. Every
  command newly refused is read. One an agent typed in ordinary work means the
  port is narrowed or withdrawn, and the row changes in `IMPORT.md`. The PR
  body gives both counts.

### Task 3: The §8 mechanisms

Harness session. Three `/ship` pull requests: the settings go last, since a
base change reaches every target's rendered file.

**Files:**
- Create (harness): `payload/.claude/hooks/run-guard.sh`, `run-guard.py`;
  `payload/.claude/skills/codebase-index/scripts/gate-index.py`, `gate.sh`;
  `payload/.claude/scripts/test_index_gate.py`,
  `payload/.claude/scripts/gh-pr-create.sh`, `test_gh_pr_create.py`
- Modify (harness): `payload/.claude/hooks/refresh-index.py`,
  `test_refresh_index.py`; `skills/codebase-index/scripts/cbx`;
  `payload/.harness/settings.base.json`; `payload/.claude/commands/pr.md`,
  `ship.md`; `docs/commands/ship.md`; `payload/docs/harness-boundaries.md`;
  `test_harness_denies.py`, `test_hook_timeouts.py`,
  `test_edit_target_guard.py`, `test_git_argv_guard.py`,
  `test_sed_crlf_guard.py`; `README.md`, whose *Tests*
  recipe types `git worktree remove --force` and takes a scratch clone instead

**Interfaces:**
- Produces: `sh "${CLAUDE_PROJECT_DIR}/.claude/hooks/run-guard.sh" <guard>`,
  the form the base's four guards take, in five commands, since
  `guard-git-argv.py` is wired for Bash and for PowerShell. The others are
  `guard-index-argv.py`, `guard-sed-crlf.py` and `guard-edit-target.py`. The
  two hints stay on `hint-bound.sh` and `refresh-index` and `index-mcp-root` on
  `runpy`.
- Produces: `index_openable(...)` in `refresh-index.py`, as `gate-index.py`
  needs it.
- Produces: `bash .claude/scripts/gh-pr-create.sh <title-file> <body-file>`,
  with the repository, head and base fixed by the helper.

- [ ] **Step 1 (index):** Port the five behaviours of *Decisions* with
  frontend's cases, `index_openable` first. Move the gate, call it from `cbx`,
  and confirm `mcp-launch.mjs` needs nothing from `run-index`, which goes. If
  it does, that call moves to `gate.sh`.
- [ ] **Step 2 (helper):** Take `gh-pr-create.sh`, with the title and body as
  path arguments, so that no target needs a new `.gitignore` line. `pr.md` and
  `ship.md` use it, granted as `gh-pr-merge.sh` is, and the raw `gh pr create`
  grant goes. Under `--dispatched` the grant holds too.
- [ ] **Step 3 (settings):** Take `run-guard.sh` and `run-guard.py`, make the
  closed set the four guards, and rewrite those commands. Add `Bash(gh pr
  merge:*)` and `Bash(git worktree remove:*)` to the base's denies. Update
  `test_hook_timeouts.py`'s count of `py` and `hint-bound` commands, restate the
  `py -3.12` floor cases of the three guard suites against the wrapper and
  `run-guard.py`'s floor, and record the no-Git-Bash residual in the boundaries.
  Cases:
  - `test_each_guard_runs_through_the_wrapper`;
  - `test_a_missing_interpreter_blocks_a_guard`, exit 2;
  - `test_an_unknown_hook_exits_2`: a guard name missing from the set must not
    pass a call unguarded, so frontend's refusal stands;
  - `test_each_raw_form_is_denied_and_the_helper_is_not`, using the
    `bash_deny_matches` the suite has.
- [ ] **Step 4:** Run Task 2's replay against the settings' denies by reading,
  over backend's and the harness's transcripts: `git worktree remove` or `gh pr
  merge` typed bare since 2026-10-19 is a stop this adds, and the PR body gives
  that count. The syncs this opens land by `/ship sync` or `/dispatch`,
  which backend and the harness both have now.

### Task 4: Each target owns its sweep rows

Harness session, closing harness #73, which frontend's first sweep would meet.
The rows are a tree fact, so they leave the managed script.

**Files:**
- Create (backend, harness, each local): `.harness/sweep-rows.tsv`
- Modify (harness): `payload/.claude/scripts/sweep-slices.sh`,
  `test_sweep_slices.py`; `payload/.claude/commands/bug-sweep.md`,
  `security-sweep.md`; `docs/commands/bug-sweep.md`, `security-sweep.md`;
  `payload/.claude/scripts/pr-locality.sh`, `test_pr_helpers.py`;
  `targets/blueprint-backend.config.json`, `.harness/config.json`;
  `IMPORT.md` *Stack mentions left verbatim*
- Modify (backend): `.harness/config.json`

**Interfaces:**
- Produces: `.harness/sweep-rows.tsv`, tab-separated: `kind`, `pattern`, `row`,
  `holds`. `kind` is `bug`, `security` or `*`. `pattern` is a POSIX extended
  regular expression over the whole path, anchored by its writer, and the first
  match wins, as the row table's arms do. Row `-` means no row owns the path,
  which is how `*/*) return 1` is written and what keeps the refusal. `holds`
  is the sentence the sweep commands read about a row. A comment line starts
  `#`.

- [ ] **Step 1:** Backend adds its file, the same rows and order as today's
  `row_of`, its root arm as `^[^/]+$` and its `*/*` arm as `^.*/.*$` with row
  `-`, with `holds` from the commands' tables, and lists it under `local`.
  Merged first: today's helper ignores it, and Review Focus 4 needs it present
  when the sync lands. The harness root does the same, with `^harness_sync/`,
  `^measure/`, `^payload/`, `^rulesets/` and `^targets/` as rows of its own.
- [ ] **Step 2:** Write failing cases in `test_sweep_slices.py`:
  - `test_a_row_comes_from_the_file`;
  - `test_a_missing_row_file_exits_3`;
  - `test_a_malformed_line_exits_3_naming_it`;
  - `test_the_first_matching_row_wins`;
  - `test_the_checkout_partitions_with_no_unowned_path`, for both kinds, over
    the checkout's own tree, so that it runs in every target. `TheRealTree` is
    in backend's local sibling, which no sync deletes, so a backend pull
    request removes it once this case has landed there.
- [ ] **Step 3:** `sweep-slices.sh` takes frontend's one awk pass, with
  `row_of` a loop over the file's patterns: no `mapfile`, no associative array,
  and no fork per file. The commands' row tables become: *the rows
  `.harness/sweep-rows.tsv` names, with what each holds*. The `IMPORT.md` line
  for #73 goes.
- [ ] **Step 4:** `pr-locality.sh` loses its `declare -A` by the same rule,
  which frontend's required `harness (macos-latest)` leg needs: `/bin/bash` on
  macOS is 3.2 (frontend `docs/testing.md`). Add
  `test_no_helper_uses_a_bash_4_construct`, over `mapfile`, `declare -A` and
  `${x,,}` in the payload's shell.
- [ ] **Step 5:** `/ship`, closing #73.

### Task 5: Frontend's config, and a rehearsal

Harness session.

**Files:**
- Create (harness): `targets/blueprint-frontend.config.json`
- Modify (harness): `harness_sync/tests/` (a case that renders it)

- [ ] **Step 1:** Write the config:
  - `repo`, `task_list`: `alexander-shamray/blueprint-frontend`,
    `.todo/blueprint-frontend.md`;
  - `settings.deny`: the 29 of *Decisions*, exact strings from frontend's
    `.claude/settings.json` at the pin;
  - `settings.allow`: `Bash(bash .claude/scripts/npm-checks.sh:*)`, since the
    payload's `/ship` carries no stack grant;
  - `local`: the *Local* rows of *Decisions*, `test_npm_checks.py`,
    `.harness/sweep-rows.tsv`, `.github/locality-gate/classes.yml`, and any
    frontend-only path Task 1's `local` rows move to. A `local` row in a
    file the payload manages moves to a new path first, as the npm cases do:
    `apply` refuses a local path the payload manages when the target differs;
  - `exclude`: `.github/output-gate/**`.
- [ ] **Step 2:** Add `test_frontend_config_renders`: the file loads and every
  one of the 29 denies is in the rendered settings, with every base deny and
  hook intact. Backend's and the harness's own rendered settings are unchanged
  by it.
- [ ] **Step 3:** Rehearse on a scratch clone of frontend `main`, as #1773 did:
  apply at the harness tip with this config, with Task 7's deletions made and
  its created files in place, the row file, `classes.yml`,
  `docs/harness-local.md` and `test_npm_checks.py`. Then run:
  - the drift gate, which must print nothing wrong;
  - `py -3.12 -m unittest discover -s .claude/scripts`;
  - each gate directory's suite;
  - `npm-checks.sh`, in the mode `/ship` uses.
- [ ] **Step 4:** A failure in a payload suite is fixed in the payload, in a
  harness pull request. One from a tree assumption in a payload case is a split
  as #5 made. Rerun until clean, and put the run's counts in that pull request.
- [ ] **Step 5:** The harness runs the whole suite on ubuntu alone, so its
  time on windows and macos is unknown until the first sync's own run. Task 7's
  script therefore raises frontend's `harness` `timeout-minutes` from 30 to 60,
  and Step 3 records the ubuntu time. A failure on either runner in Task 7 is
  fixed in the payload and re-applied.
- [ ] **Step 6:** `/ship` the config. The harness side is now done, and
  `targets.json` does not list frontend yet.

### Task 6: Frontend's closure gate

Frontend session, before the first sync, as README says.

- [ ] **Step 1:** Confirm there is no open harness branch in frontend:
  `gh pr list`, then `git log origin/main..<branch>` for each non-Dependabot
  branch not already merged. One that holds harness work lands now or is
  re-made in the payload.
- [ ] **Step 2:** `.github/closure-gate/` is denied here, so write an owner-run
  script copying the three payload files at the harness tip byte for byte. No
  workflow lands here: `closure-gate.yml` comes with the sync, which is why the
  gate must already be on `main`. `/ship`, Class D, merge on green.

### Task 7: The first sync, one pull request

Frontend session, on branch `chore/harness-first-sync`. Not
`chore/harness-sync-<sha7>`, which is the bot's and which `sync_pr.py list`
would take. From this branch's creation to its merge frontend's `.claude/` is
frozen.

**Files (frontend):**
- Create: `.harness/config.json` (from Task 5), `.harness/sweep-rows.tsv`,
  `.github/locality-gate/classes.yml`, `docs/harness-local.md`,
  `.claude/scripts/test_npm_checks.py`
- Delete: every frontend path the *Decisions* drop, in the same script as the
  apply, since frontend's old settings call `index-refresh.py` until then
- Modify: `.gitignore`, `docs/testing.md:83`, `docs/change-locality.md` §3 and
  §1's trust list if it names a moved owner
- Written by the apply: every managed path, `.harness/manifest.json`, the
  rendered `.claude/settings.json`

- [ ] **Step 1:** Re-read the range:
  `git diff --stat 41a3f45 origin/main -- .claude .github docs/commands docs/harness-boundaries.md`.
  Anything there goes back through Task 1's verdicts, and to Task 2 if `port`,
  before this branch goes on.
- [ ] **Step 2:** Write `docs/harness-local.md`, the four headings:
  - *Documents*: change contract `docs/change-locality.md`; style guide
    `docs/style-guide.md`; testing `docs/testing.md`; product specification
    `docs/client-architecture.md`, then the design spec; roadmap none; closed
    records none; measurements `docs/token-usage.md`.
  - *Ship checks*: `bash .claude/scripts/npm-checks.sh`, and the required
    checks, as the live ruleset names them: `android`, `audit`, `commit-gate`,
    `commits`, `e2e`, `harness (macos-latest)`, `harness (ubuntu-latest)`,
    `harness (windows-latest)`, `ios-build`, `ios-config`, `web`.
  - *Residuals*: cluster F's `residual` rows.
  - *Review examples*: from `.claude/commands/review-branch.md` *What counts
    as a finding*.
- [ ] **Step 3:** Write `.harness/sweep-rows.tsv` from frontend's own awk
  `row_of` in `sweep-slices.sh`, keeping the function's test order, a `.spec.ts`
  before `core` and `screens`, so a first match gives what it gives today. The
  rows are the set `core`, `screens`, `suites`, `tooling`, `native`, `samples`;
  for security `source`, `tooling`, `native`, `samples`. `tooling` gains
  `.harness/`, which the function does not know.
- [ ] **Step 4:** Write `.github/locality-gate/classes.yml` in backend's
  grammar from §3's table, adding `.harness/**` to Class D as backend's map
  has it, since every sync's manifest is there. §3 then cites the file as the
  owner of the tree sets, and its gap paragraph keeps saying there is no gate in
  CI, which stays true.
- [ ] **Step 5:** Move the npm cases to `test_npm_checks.py` by the script, and
  add `.claude/skills/codebase-index/.skill_version` to `.gitignore`.
- [ ] **Step 6:** Write one owner-run script, under agent-rules *Handing over
  to the owner*, with every `.claude/` and `.github/` change: copies, deletions
  by `git rm`, then the apply from a harness export at a named sha, settings
  last. Its dry run names each file.
- [ ] **Step 7:** Verify by reading: the manifest's commit; the drift gate
  clean; no path under `MANAGED_DIRS` in neither list; the rendered settings
  hold the 29 and every base deny; the local suites and `npm-checks.sh` pass.
- [ ] **Step 8:** `/ship`, Class D, one `branch-reviewer` round on the
  target-specific part: config, local files, *Ship checks*, the rendered
  settings, the deletions. Not the payload blobs, which the drift gate pins. A
  red `comment-gate` is not a required check here; report it, comment on
  harness #80 and go on. Merge on green.
- [ ] **Step 9:** The owner relaunches any frontend session.

### Task 8: Frontend joins the sync

Harness session, owner steps named.

**Files (harness):**
- Modify: `targets.json`, `README.md`, `rulesets/main.json`,
  `harness_sync/tests/test_ruleset.py`, which asserts the targets exactly
- Modify (frontend, by an owner script): `.github/workflows/ci.yml`

- [ ] **Step 1 (owner):** Add frontend to `HARNESS_SYNC_TOKEN`'s repositories,
  with Contents, Pull requests and Workflows read-write.
- [ ] **Step 2:** Add frontend to `targets.json`; `/ship`. `sync.yml`'s path
  filter is `payload/**` and `harness_sync/**`, so start a run by
  `workflow_dispatch` unless the merge touched either. Confirm it reads
  *current* or opens a sync. A red run for frontend with a token error is Step 1
  undone; no other target is affected (`fail-fast: false`).
- [ ] **Step 3:** Frontend's `ci.yml` runs each new gate directory's suite, in
  a job the harness's `payload-suites` gate step shapes, added to `main-red`'s
  `needs`. Owner script, `/ship`.
- [ ] **Step 4:** Add `closure-gate` and `harness-drift` to frontend's list in
  `rulesets/main.json` and to its *Ship checks*, the second first, so that
  `/ship` never waits on a list short of the ruleset. The owner runs
  `ruleset-apply`, and `ruleset-check --effective` must read `matches`, exit 0.
  Backend's `harness-drift` goes with plan 6's *Drift gate on in all three*.

### Task 9: Frontend's contract and memory

Frontend session, as plan 2's Tasks 8 and 9.

**Files (frontend, Class D):**
- Modify: `CLAUDE.md`, `AGENTS.md` (by a script, it is denied),
  `docs/change-locality.md` §6, `docs/token-plan.md`

- [ ] **Step 1:** `CLAUDE.md` gains `@.harness/agent-rules.md` and keeps what
  describes the product. Its owner table gains the managed documents. §6's
  moved bullets become one line citing `docs/harness/working-rules.md`; what
  only frontend has stays. `docs/token-plan.md` cites agent-rules for each rule
  it states.
- [ ] **Step 2:** Triage frontend's 23 memories as plan 2's Table M did, by a
  table in this PR's body: archived, rewritten, or kept with why. A rule the
  payload now states is archived. A rule held only in part stays and is filed
  against harness #48. Report `MEMORY.md`'s bytes before and after.
- [ ] **Step 3:** `/ship`.

### Task 10: Prove it, and record it

- [ ] **Step 1:** The next harness payload change reaches frontend as an
  ordinary sync. Take it with `/ship sync #<n>`; the verify line goes in
  frontend's task list. A sync frontend's `/dispatch` takes on a night when it
  has work is evidence too, and the plan does not wait for one.
- [ ] **Step 2:** Run `/bug-sweep` once in frontend, to one round. A refusal
  from `sweep-slices.sh` here is a Task 4 defect, filed in the harness.
- [ ] **Step 3:** Add *After plan 5* to harness `docs/baseline-2026-11.md`:
  frontend's stop count from `measure/stops.py --command ship` since Task 7's
  merge, once two sessions have ended, and frontend's load bytes as *After plan
  2* gives backend's. A rise against the two tables is a harness defect, filed
  there, read like with like.
