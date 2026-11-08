# Shared harness 6: admin's first sync, and the drift gate on in all three — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** `blueprint-admin` runs on the shared harness, every fix only its
copy holds is in the payload or recorded as refused, and then the drift gate
is a required check in all three targets and the porting rules it makes
obsolete are gone.

**Architecture:** Spec §7 step 3 for admin, then step 4. Plan 5's shape,
which frontend #382 proved: harness work first (an audit of the diverged
files, the ports it calls for, the §8 mechanisms), then a rehearsal on a
scratch clone, then one admin pull request holding the config, the local
files, the deletions and the apply together, because a drift gate refuses any
of them alone. Step 4 closes the rollout and §9 is measured.

**Tech Stack:** Python 3.12, bash, `harness_sync`, GitHub rulesets. Admin's
stack (.NET host, Angular SPA, npm, `pin-gate`) stays admin's.

**Spec:** `docs/superpowers/specs/2026-11-02-shared-harness-design.md`, §5.2,
§5.4, §5.5, §7 steps 3–4, §8, §9. Plans 1–4 are done; plan 5's Tasks 1–7 have
landed (frontend #382). This plan's harness tasks start from harness `main` at
`58013f6` or later, and admin is pinned at `c5ec185c` for the audit. Tasks 7
and 8 wait for plan 5's Task 8, which edits the same `targets.json` and
`rulesets/main.json`.

## The state this starts from

Read on 2026-11-08, admin `c5ec185c` against harness `58013f6`:

| | Count |
|---|---|
| Payload files | 166 |
| Equal in admin, LF-normalised | 3 |
| Present in both, differing | 64 |
| Absent from admin | 99 |
| Tracked by admin in the harness surface, absent from the payload | 33, `.claude/settings.json` among them |
| `settings.json` denies admin has and the base lacks | 51 |
| Base denies admin lacks | 8 |
| `settings.json` allows admin has and the base lacks | 0 |
| Admin auto-memories, `MEMORY.md` aside | 16 (`MEMORY.md` 2986 bytes) |

The guards diverge most: admin's `guard-git-argv.py` is 7649 lines against the
payload's 3402, with 100 functions only admin's has and 33 only the payload's;
`guard-edit-target.py` has 9 and 4. `test_harness_helpers.py` holds 376 cases.

The rest of what this plan depends on:

- `.gitignore` already ignores `.claude/worktrees/` and
  `.claude/skills/codebase-index/.skill_version`.
- Admin's `main` has no `.github/closure-gate/`, so README's *sibling lands
  its closure gate first* rule applies.
- Admin has `.github/locality-gate/classes.yml`, which the payload's
  `pr-locality.sh` needs; its Class D lacks `.harness/**`.
- Admin's live ruleset `main` (id 23805274) holds the floor and the eleven
  checks `rulesets/main.json` lists for admin, read back on 2026-11-08. That is
  §5.5's precondition for the client relaxation the sync brings.
- Admin builds into `artifacts/` (`Directory.Build.props:13-14`), so the
  payload's output gate holds there and is not excluded.
- Admin's settings deny `.claude/{agents,commands,hooks,scripts,skills}/**`
  and `settings*.json`, and not `.github/**`.
- Three spellings of the red-`main` issue: backend `main is red: <workflow>`,
  frontend `CI is red on main`, admin `main is red`.
- Harness #191: the whole payload suite fails on macOS (132) and Windows (10)
  runners. Admin's required `harness` checks run on all three today. Frontend's
  `harness` job runs the whole suite on ubuntu and the harness's `legs`
  elsewhere; on #382 the whole suite took 3m42 on ubuntu and 15m45 on Windows.
- Admin has no open pull request; its remote `chore/*` branches are checked by
  Task 6.

## Global Constraints

- "Every managed file is common; a repository differs only through its own
  setting, never by replacing a managed file." (spec §5.2)
- Additions "may add allows, denies, hooks and MCP servers" but "never remove
  or narrow a base deny or hook". (spec §5.2)
- "Each repository's `main` stays green at every step of the rollout."
  (spec §6)
- "A target's own `.claude/` is frozen from the moment its first sync is
  opened until it merges." Harness branches open in admin land first or are
  re-made against the payload. (spec §7)
- "In each repository the ruleset is confirmed by reading it back before that
  repository's client deny on force is relaxed." (spec §5.5)
- Security, in the owner's words: "главное не портить мэйн", and the plan
  fails "если сломается main или агенты станут чаще спрашивать".
- A guard behaviour is ported only if it passes the three criteria of
  `docs/harness-boundaries.md`, *The guards' threat model*. A spelling built
  only to hide a command from a guard is recorded as out of scope and not
  ported.
- A managed file is changed in `payload/`, never in admin.
- In an admin session `.claude/**` is denied. Every edit there goes in an
  owner-run script under agent-rules *Handing over to the owner*, never round
  the deny. `.github/**` is editable there, and the config adds no deny on it,
  since that would be a new stop.
- Admin-local test files are named `test_<module>_admin.py`, as backend's are
  `_backend` and frontend's `_frontend`.
- Merge bar: the `/ship` loop for harness and admin pull requests. The first
  sync takes one review round, on the target-specific part.

## Decisions on the §8 mechanisms and admin-only files

Read on 2026-11-08 at the pins above. Each row is recorded in `IMPORT.md` by
Task 1.

| Admin path | Decision | Why, and what moves |
|---|---|---|
| `hooks/index-hint-run.sh` | Drop | The payload's `hint-bound.sh` is the same launcher: a bound shorter than the harness's, exit 0 on every path. Cluster D compares the bound and any refusal |
| `hooks/index-query-hint.sh` | Drop; cluster D ports behaviours to `index-query-hint.py` | The payload has the hook in Python; `test_index_query_hint.py` is in both and differs |
| `hooks/refresh-index.sh`, `test_index_refresh_hook.py` | Drop; cluster D ports behaviours to `refresh-index.py` with admin's cases | The payload's hook already took frontend's five behaviours (#154) |
| `shard-harness-suite.py`, `test_harness_shards.py` | Payload, with `.claude/scripts` its one discovery root | It cuts the serial Windows run of about seventeen minutes, which every target that runs the whole suite pays, in `/ship` as in CI. Its placement comes from discovery, with no list written by hand. Admin's other roots are run by `harness-checks.sh` |
| `main-red-open.sh` | Payload, with the title rule of Task 3 | It guards `main`: a pull request's own checks are pinned to their merge ref, so a red `main` reads green (admin #531). The payload's `/ship` step 6 reads only the pull request's checks |
| `harness-checks.sh`, `host-checks.sh`, `spa-ci.sh`, `npm-checks.sh` | Local, granted through config | They are the stack, and `harness-checks.sh` names admin's gate suites |
| `skills/admin-invariants/SKILL.md` | Local | Product invariants, under a managed directory |
| `.github/pin-gate/**`, `.github/locality-gate/**`, `workflows/ci.yml`, `locality-gate.yml`, `main-red.yml`, `dependabot.yml` | Local by position | Outside the managed directories and not in the payload; no config line needed |
| `gh-issue-filing.sh`, `gh-sweep-issue-create.sh` | Drop; cluster C judges | As frontend: the payload's `gh-issue-create.sh` has the routes |
| `test_pr_locality.py` | Cluster C judges; the gate-parity cases stay as `test_pr_locality_admin.py` | It asserts the shell copy against admin's `locality_gate.py` |
| `test_index_skill_examples.py` | Cluster D judges | It reads the skill's examples, which the payload owns |
| `test_root_ignore_coverage.py` | Local | It reads admin's generated trees |
| `test_harness_helpers.py` | Split by cluster; stack cases to `test_harness_helpers_admin.py` | Its cases for payload files are audited with their files; the cases for the four local scripts and `pin-gate` stay |
| `commands/review-branch.md`, `docs/commands/review-branch.md` | Local | As backend and frontend |
| `.github/output-gate/**` | Payload, not excluded | Admin builds into `artifacts/`; its invocation goes into admin's `ci.yml` by Task 7 Step 4 |

Two builder's calls on the settings:

- **The 51 admin denies stay config additions**, `Bash(git *--no-index*)`,
  `Edit(AGENTS.md)` and the MSBuild and NuGet set among them.
- **The 8 base denies admin lacks come in unchanged.** Four are `.git` and
  `.codeindexignore` edits; `Bash(gh pr merge:*)` and `Bash(git worktree
  remove:*)` are plan 5's. Task 3 Step 5 counts what they would have stopped
  in admin's transcripts.

## Review Focus

1. A ported guard refusal stops a command admin's agents type every day: the
   plan's anti-goal, and cluster A here is twice frontend's. Task 2's replay
   reads admin's transcripts and any new refusal stops the port.
2. An admin-only file under a managed directory is in neither the manifest
   nor `local`: the drift gate is red on the first sync. Task 4's rehearsal
   and Task 7's check list every such path first.
3. Admin's required `harness (windows-latest)` and `harness (macos-latest)`
   run the whole payload suite and meet #191 on the sync pull request. Task 7
   gives admin's job frontend's shape and records it on #191.
4. The payload's `main-red-open.sh` matches one title spelling, and a target
   whose workflow writes another reads green while `main` is red: the helper
   fails open. Task 3 pins the rule and retitles frontend's workflow before
   the helper reaches it.
5. An admin session open when the first sync merges still holds the old
   settings, which call `index-hint-run.sh` and `refresh-index.sh`, both
   deleted. They are `PreToolUse` hints and `PostToolUse`, `SessionStart` and
   `UserPromptSubmit` hooks; a missing file exits 127, which reports an error
   and blocks nothing. The guards keep `run-guard.sh`, which the sync
   rewrites. Task 7 relaunches.

---

### Task 1: Audit the diverged files

Harness session. Spec §7 step 3: "one audit per file cluster, by read-only
subagents, returning `file:line` and the fix".

**Files:**
- Modify (harness): `IMPORT.md`, a new section *Admin audit, pinned at
  c5ec185c*, in the row form of *Frontend audit, pinned at 41a3f45*

Clusters:

| | Cluster | Admin paths |
|---|---|---|
| A | Guards | `hooks/guard-git-argv.py`, `guard-edit-target.py`, `test_edit_target_guard.py`, the guard cases in `test_harness_helpers.py` |
| B | Git helpers | `git-*.sh`, `test_git_rebase_onto_main.py`, `test_git_worktree_fork.py`, their cases in `test_harness_helpers.py` |
| C | GitHub helpers | `gh-*.sh`, `pr-*.sh`, `sweep-slices.sh`, `test_git_pr_trailer_helpers.py`, `test_sweep_slices.py`, `test_pr_locality.py` |
| D | Index | `hooks/index-*`, `refresh-index.sh`, `skills/codebase-index/**`, `test_index_*.py` |
| E | Commands | `commands/*.md`, `agents/*.md`, `docs/commands/*.md`, `test_command_runbooks.py` |
| F | Docs | `docs/harness-boundaries.md`, `docs/working-rules.md`, `docs/token-plan.md`, `docs/token-quality-sets.md` |
| G | Gates | `.github/commit-gate/`, `issue-form-gate/`, `ISSUE_TEMPLATE/finding.yml`, `workflows/commit-gate.yml` |

- [ ] **Step 1:** Spawn one read-only agent per cluster, with the two trees at
  their pins, this plan's *Decisions* and `IMPORT.md`'s frontend rows, so that
  a behaviour already ported from frontend reads `covered`. Each returns rows
  of: admin `file:line`, the behaviour, whether the payload has it
  (`file:line`), a verdict and its one-line reason. Cluster A starts from the
  100 and 9 functions only admin's guards have, and the 33 and 4 only the
  payload's have, and splits by guard if one agent cannot hold both.
- [ ] **Step 2:** Verdicts are plan 5's closed set: `port`, `covered`, `out of
  scope`, `local`, `obsolete`. Cluster F also has `residual`, for admin's
  `docs/harness-local.md` *Residuals*, and `managed`, for a paragraph of
  `docs/working-rules.md`, `token-plan.md` or `token-quality-sets.md` the payload's
  `docs/harness/*.md` already states, which Task 9 then cites.
- [ ] **Step 3:** Check each cluster A `port` row against the three criteria
  of *The guards' threat model* by hand, and each `covered` row by reading the
  cited line. An agent's verdict is not the record.
- [ ] **Step 4:** Write the rows to `IMPORT.md` and `/ship` that alone. The
  count of `port` rows per cluster goes in the PR body.

### Task 2: Port what the audit found

Harness session. One `/ship` pull request per cluster holding a `port` row,
in the order G, B, C, D, E, A.

**Files:**
- Modify (harness): `payload/` files by cluster, `IMPORT.md`

**Interfaces:**
- Consumes: `py -3.12 measure/replay_guard.py --project <glob> --since <date>
  --guard <hook path>`, from plan 5 Task 2.

- [ ] **Step 1:** Each `port` row takes admin's own test as its failing case,
  adapted to the payload's suite. Confirm it fails against the payload, then
  port, then confirm it passes. A row with no admin test gets one.
- [ ] **Step 2:** Mark each row `ported` with the commit in `IMPORT.md`, in
  the same pull request.
- [ ] **Step 3:** For cluster A, run the replay with the payload guard before
  and after the port, over `C--dev-ashamray-blueprint-backend*`,
  `C--dev-ashamray-blueprint-frontend*` and `C--dev-ashamray-blueprint-harness*`
  since 2026-10-19. Every command newly refused is read; one an agent typed in
  ordinary work means the port is narrowed or withdrawn, and the row changes in
  `IMPORT.md`. The PR body gives both counts per project.
- [ ] **Step 4:** Run the replay once more with the payload guard at the
  cluster A tip over `C--dev-ashamray-blueprint-admin*` since 2026-10-19,
  against admin's own guard over the same range. A command admin's guard lets
  through and the payload's refuses is a stop the sync adds: it is read, and
  either the payload is narrowed or the PR body names it with why it stays.

### Task 3: The §8 mechanisms

Harness session, and one frontend pull request. Three harness `/ship` pull
requests: the shard runner, the red-`main` helper, then the settings replay.

**Files:**
- Create (harness): `payload/.claude/scripts/shard-harness-suite.py`,
  `test_harness_shards.py`, `main-red-open.sh`, `test_main_red_open.py`
- Modify (harness): `payload/.claude/commands/ship.md`,
  `payload/docs/commands/ship.md` (step 6), `payload/.harness/agent-rules.md`
  if it names how a red `main` is seen, `.github/workflows/ci.yml` (the
  harness's own `payload-suites` ubuntu leg runs through the runner),
  `IMPORT.md`
- Modify (frontend, by an owner script): `.github/workflows/ci.yml`, the
  `main-red` job's title

**Interfaces:**
- Produces: `py -3.12 .claude/scripts/shard-harness-suite.py`, no arguments,
  discovering `.claude/scripts` alone; exit 0 only when every discovered id ran
  and passed.
- Produces: `bash .claude/scripts/main-red-open.sh`, no arguments. It lists
  open `bug` issues and exits 3 printing `main is red: #<n> <url>` when one's
  title is exactly `main is red` or starts with `main is red: `; 0 printing
  nothing when none is; 2 when the listing fails or a page comes back full.
  `/ship` step 6 runs it before the merge, granted bare, never `:*`.

- [ ] **Step 1 (shard):** Take admin's runner and its suite, with the roots cut
  to `.claude/scripts`. Admin's cases are the failing ones; add
  `test_the_only_root_is_the_scripts_directory`. Run the payload suite through
  it on ubuntu in the harness's CI, and give the serial and sharded times in
  the PR body.
- [ ] **Step 2 (frontend title):** Frontend's `main-red` job writes `main is
  red: ci`, and an open `CI is red on main` issue, if any, is retitled. Owner
  script, `/ship` in frontend, merged before Step 3's pull request.
- [ ] **Step 3 (helper):** Take admin's helper with the title rule above. Cases:
  - `test_an_open_bare_title_exits_3`;
  - `test_a_workflow_suffixed_title_exits_3`, `main is red: ci`;
  - `test_a_title_that_only_contains_the_phrase_exits_0`, `why main is red
    yesterday`;
  - `test_a_failed_listing_exits_2`;
  - `test_a_full_page_exits_2`;
  - `test_ship_runs_the_helper_bare`, admin's grant case from
    `test_harness_helpers.py:10498-10501`.
  Confirm each target's `main-red` workflow writes a title the rule matches,
  and record the three in the PR body.
- [ ] **Step 4:** Mark the §8 rows `ported` in `IMPORT.md`.
- [ ] **Step 5 (settings):** No base change. Count by reading admin's
  transcripts since 2026-10-19 the commands typed bare that the 8 base denies
  admin lacks would refuse. The count goes in the config's pull request body
  (Task 4); a non-zero count is read, and a command an agent needs in ordinary
  work gets its helper named in `/ship` or `docs/harness/working-rules.md`
  before Task 7.

### Task 4: Admin's config, and a rehearsal

Harness session.

**Files:**
- Create (harness): `targets/blueprint-admin.config.json`
- Modify (harness): `harness_sync/tests/test_target_configs.py`

- [ ] **Step 1:** Write the config:
  - `repo`, `task_list`: `alexander-shamray/blueprint-admin`,
    `.todo/blueprint-admin.md`;
  - `settings.env`: nothing; admin's two keys are the base's;
  - `settings.deny`: the 51, exact strings from admin's `.claude/settings.json`
    at the pin;
  - `settings.allow`: `Bash(bash .claude/scripts/host-checks.sh)`,
    `Bash(bash .claude/scripts/spa-ci.sh)`,
    `Bash(bash .claude/scripts/harness-checks.sh)`,
    `Bash(bash .claude/scripts/npm-checks.sh:*)`, in the forms admin's
    `ship.md` grants them;
  - `local`: the *Local* rows of *Decisions*, `.claude/scripts/test_*_admin.py`,
    `.claude/scripts/local_commands.py` if Task 1 cluster E needs one,
    and any admin-only path Task 1's `local` rows move to. A `local` row in a
    file the payload manages moves to a new path first: `apply` refuses a local
    path the payload manages when the target differs;
  - `exclude`: nothing.
- [ ] **Step 2:** Add `test_admin_config_renders`: the file loads, every one of
  the 51 denies and the four allows is in the rendered settings, with every
  base deny and hook intact. Backend's, frontend's and the harness's rendered
  settings are unchanged by it.
- [ ] **Step 3:** Rehearse on a scratch clone of admin `main`, as #382 did:
  apply at the harness tip with this config, with Task 7's deletions made and
  its created files in place. Then run:
  - the drift gate, which must print nothing wrong;
  - `bash .claude/scripts/harness-checks.sh`, and its time;
  - each gate directory's suite, `pin-gate` and `locality-gate` included;
  - `host-checks.sh`, `spa-ci.sh` then `npm-checks.sh`, in the modes `/ship`
    uses.
- [ ] **Step 4:** A failure in a payload suite is fixed in the payload, in a
  harness pull request; one from a tree assumption in a payload case is split
  to a local sibling as #5 made. Rerun until clean, and put the counts in that
  pull request. A failure that is #191's goes on #191 as a comment, not here.
- [ ] **Step 5:** `/ship` the config. `targets.json` does not list admin yet.

### Task 5: Admin's closure gate

Admin session, before the first sync, as README says.

- [ ] **Step 1:** Copy the three payload files of `.github/closure-gate/` at
  the harness tip byte for byte. `.github/**` is editable here, so no owner
  script is needed. No workflow lands: `closure-gate.yml` comes with the sync,
  which is why the gate must already be on `main`.
- [ ] **Step 2:** `/ship`, Class D, merge on green.

### Task 6: No harness work open in admin

Admin session, the day Task 7 starts.

- [ ] **Step 1:** `gh pr list` must be empty of harness work. For each remote
  branch not Dependabot's, `git log origin/main..<branch>`: a branch with no
  commit beyond `main` is listed as spent; one holding harness work lands now
  or is re-made in the payload, named in Task 7's PR body.
- [ ] **Step 2:** Re-read the range:
  `git diff --stat c5ec185c origin/main -- .claude .github docs/commands docs/harness-boundaries.md docs/working-rules.md`.
  Anything there goes back through Task 1's verdicts, and to Task 2 if `port`,
  before Task 7 starts.

### Task 7: The first sync, one pull request

Admin session, on branch `chore/harness-first-sync`. From its creation to its
merge admin's `.claude/` is frozen.

**Files (admin):**
- Create: `.harness/config.json` (from Task 4), `.harness/sweep-rows.tsv`,
  `docs/harness-local.md`, `.claude/scripts/test_*_admin.py`
- Delete: every admin path the *Decisions* and Task 1 drop, in the same script
  as the apply
- Modify: `.github/locality-gate/classes.yml` (Class D gains `.harness/**`),
  `.github/workflows/ci.yml` (the `harness` job, Step 5),
  `docs/change-locality.md` §1's trust list if it names a moved owner,
  `docs/testing.md` where it names a dropped file
- Written by the apply: every managed path, `.harness/manifest.json`, the
  rendered `.claude/settings.json`

- [ ] **Step 1:** Write `docs/harness-local.md`, the four headings:
  - *Documents*: change contract `docs/change-locality.md`; style guide
    `docs/style-guide.md`; testing `docs/testing.md`; product specification as
    `CLAUDE.md` *What this repo is* names it; measurements
    `docs/token-usage.md`, and `docs/token-quality-sets.md` unless cluster F
    found it `managed`.
  - *Ship checks*: `host-checks.sh`, `spa-ci.sh` then `npm-checks.sh`,
    `harness-checks.sh`, and the required checks as the live ruleset names
    them: `commit-gate`, `commits`, `drift`, `harness (macos-latest)`,
    `harness (ubuntu-latest)`, `harness (windows-latest)`, `host
    (ubuntu-latest)`, `host (windows-latest)`, `locality-gate`, `smoke`, `web`.
  - *Residuals*: cluster F's `residual` rows.
  - *Review examples*: from `.claude/commands/review-branch.md` *What counts
    as a finding*.
- [ ] **Step 2:** Write `.harness/sweep-rows.tsv` from admin's `row_of` in
  `sweep-slices.sh`, in its order, so a first match gives what it gives today:
  for `bug`, `config` (the two `*.csproj`), `host`, `suites`, `spa`, `config`
  (the rest of `src/Admin.Web/`); for `security`, `source`; for `*`, `tooling`
  over `tools/`, `.github/`, `.claude/` and `.harness/`, which the function
  does not know, then `samples` over `docs/`; then the `*/*` arm as `^.*/.*$`
  with row `-`, and the root arm as `^[^/]+$` with row `config`.
- [ ] **Step 3:** `harness-checks.sh` calls the payload's
  `shard-harness-suite.py`, then runs `.github/locality-gate`,
  `.github/pin-gate` and `tools/token-usage` serially, each refusing an empty
  discovery. Admin's stack cases move from `test_harness_helpers.py` into
  `test_harness_helpers_admin.py`, and the parity cases into
  `test_pr_locality_admin.py`.
- [ ] **Step 4:** `ci.yml`'s `harness` job takes frontend's shape: the runner
  on ubuntu, the modules `python -m harness_sync legs` names elsewhere, until
  #191 closes; the three check names do not change. Its invocation of the
  output gate is added beside `pin-gate`'s. Comment on #191 that admin's legs
  now run the subset, with the run link.
- [ ] **Step 5:** Write one owner-run script, under agent-rules *Handing over
  to the owner*, with every `.claude/` change: copies, deletions by `git rm`,
  then the apply from a harness export at a named sha, settings last. Its dry
  run names each file. The `.github/` and `docs/` changes are ordinary edits.
- [ ] **Step 6:** Verify by reading: the manifest's commit; the drift gate
  clean; no path under `MANAGED_DIRS` in neither list; the rendered settings
  hold the 51, the four allows and every base deny; `harness-checks.sh` and
  the stack checks pass; `ruleset-check --effective` for admin reads `matches`,
  exit 0, since this merge relaxes the client's force deny (spec §5.5).
- [ ] **Step 7:** `/ship`, Class D, one `branch-reviewer` round on the
  target-specific part: config, local files, *Ship checks*, the rendered
  settings, the deletions, `ci.yml`. Not the payload blobs, which the drift
  gate pins. A red `comment-gate` is not a required check here; report it,
  comment on harness #80 and go on. Merge on green.
- [ ] **Step 8:** The owner relaunches any admin session.

### Task 8: Admin joins the sync

Harness session, owner steps named. After plan 5's Task 8.

**Files (harness):**
- Modify: `targets.json`, `README.md`, `rulesets/main.json`,
  `harness_sync/tests/test_ruleset.py`, which asserts the targets exactly
- Modify (admin): `.github/workflows/ci.yml`

- [ ] **Step 1 (owner):** Add admin to `HARNESS_SYNC_TOKEN`'s repositories,
  with Contents, Pull requests and Workflows read-write.
- [ ] **Step 2:** Add admin to `targets.json`; `/ship`. Start a `sync.yml` run
  by `workflow_dispatch` unless the merge touched `payload/**` or
  `harness_sync/**`, and confirm it reads *current* or opens a sync. A red run
  for admin with a token error is Step 1 undone.
- [ ] **Step 3:** Admin's `ci.yml` runs each new gate directory's suite, in a
  job the harness's `payload-suites` gate step shapes, added to `main-red.yml`'s
  watched set. `/ship` in admin.
- [ ] **Step 4:** Add `closure-gate` and `harness-drift` to admin's list in
  `rulesets/main.json` and to its *Ship checks*, the second first, so that
  `/ship` never waits on a list short of the ruleset. The owner runs
  `ruleset-apply`, and `ruleset-check --effective` must read `matches`, exit 0.

### Task 9: Admin's contract and memory

Admin session, as plan 2's Tasks 8 and 9 and plan 5's Task 9.

**Files (admin, Class D):**
- Modify: `CLAUDE.md`, `AGENTS.md`, `docs/change-locality.md` §6,
  `docs/token-plan.md`, `docs/working-rules.md`

- [ ] **Step 1:** `CLAUDE.md` gains `@.harness/agent-rules.md` and keeps what
  describes the product; *Subagents, review rounds and plans* and the harness
  half of *Hard rules* become citations. Its owner table gains the managed
  documents. §6's moved bullets become one line citing
  `docs/harness/working-rules.md`. Cluster F's `managed` paragraphs of
  `docs/working-rules.md` and `docs/token-plan.md` become citations; what only
  admin has stays. Report `CLAUDE.md`'s bytes before and after.
- [ ] **Step 2:** Triage admin's 16 memories as plan 2's Table M did, by a
  table in this PR's body: archived, rewritten, or kept with why. A rule the
  payload now states is archived. A rule held only in part stays and is filed
  against harness #48. Report `MEMORY.md`'s bytes before and after.
- [ ] **Step 3:** `/ship`.

### Task 10: The drift gate on in all three

Harness session, then one pull request per target. Spec §7 step 4.

**Files:**
- Modify (harness): `rulesets/main.json`, `README.md`, `IMPORT.md`
- Modify (each target): `docs/harness-local.md` *Ship checks*, and what Step 3
  finds

- [ ] **Step 1:** Confirm `harness-drift` has run green on every pull request
  of the last week in backend, frontend and admin, by `gh run list
  --workflow harness-drift`. A red one is read first: a local edit is moved to
  the payload or declared local before the check is required.
- [ ] **Step 2:** Add `harness-drift` to backend's list in `rulesets/main.json`
  and its *Ship checks*, as Task 8 Step 4 did for admin. The owner runs
  `ruleset-apply`; `ruleset-check --effective` reads `matches` for all four
  repositories.
- [ ] **Step 3:** Find the porting rules the gate makes obsolete: in each
  target's `CLAUDE.md`, `docs/` and `docs/change-locality.md`, and in the four
  projects' memories (`C--dev-ashamray-blueprint-*`, worktree directories
  included), every rule that says to carry a harness change to a sibling, keep
  copies aligned, or check a sibling's copy. Backend's memories that mention a
  sibling (`gh-checks-watch-right-after-push`,
  `moved-text-agents-can-corrupt-samples`, `never-head-limit-a-review-query`,
  `one-branch-per-service-plan`, `python-subprocess-bash-is-wsl`) and
  frontend's `npm-test-is-ng-test` are read for it. Each is removed, or kept
  with why, in a table in the PR body of the repository it is in; memories
  are removed only after their rule has landed (spec §5.7).
- [ ] **Step 4:** `IMPORT.md` gains *Rollout closed*: the date, and for each
  target its first sync's pull request and the payload commit. README's rule
  that a sibling lands its closure gate first goes, since every target has it.
- [ ] **Step 5:** `/ship` each.

### Task 11: Prove it, and record it

- [ ] **Step 1:** The next harness payload change reaches backend, frontend
  and admin as three ordinary syncs from one harness pull request (spec §9).
  Take each with `/ship sync #<n>` or `/dispatch`; the three verify lines go in
  each target's task list.
- [ ] **Step 2:** Run `/bug-sweep` once in admin, to one round. A refusal from
  `sweep-slices.sh` is a Task 7 Step 2 defect, fixed in admin's row file.
- [ ] **Step 3:** Add *After plan 6* to harness `docs/baseline-2026-11.md`:
  each repository's stop count from `measure/stops.py --command ship` since its
  first sync, once two sessions have ended there, and each one's load bytes as
  *After plan 2* gives backend's. A rise against the baseline is a harness
  defect, filed there, read like with like. No red required check on any
  `main` during the rollout is checked by `gh run list --branch main` over the
  plan's dates, and the result is recorded with it.
