# Shared harness plan 3: `main`'s rulesets and the force-push policy

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** every repository's `main` is protected by a ruleset the harness
holds as data and reads back. After that, the client policy narrows from
"no force anywhere" to "nothing that reaches `main`", so an agent can
force-push its own branch and drop its own merged branch through a helper.

**Architecture:** the rulesets are a JSON file in the harness repository's
root, outside `payload/`, because they describe the four repositories rather
than a target's tree. `harness_sync` gains a `ruleset` module and two
subcommands: one reads a ruleset back and compares it with the data, the
other applies the data. A harness CI workflow runs the read-back for all
four repositories. Only once every read-back is green does the payload gain
two lease-bound helpers and their grants. Its raw denies stay, and a single
table in `docs/harness-boundaries.md` lists every destructive class, each
with the test that proves it is refused.

**Tech Stack:** Python 3.12 standard library, `gh api`, GitHub repository
rulesets, Bash helpers, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-02-shared-harness-design.md` §2
goal 3, §4, §5.1 *Its own `main`*, §5.5. Plan 2,
`docs/superpowers/plans/2026-11-03-shared-harness-2-rules-docs-memory.md`,
is done. Its *Scope* gives this plan the rulesets and force-push.

**Where it runs:** Tasks 1–5 in a `blueprint-harness` session, and Task 6
in a `blueprint-backend` session.

**Scope.** Out of scope:
- **The siblings' client relaxation.** Frontend and admin take it at their
  first sync, plans 5 and 6. Their rulesets are in scope here, so their
  `main` is never guarded by the client alone.
- **The `/dispatch` grouping of sync PRs.** That is plan 4.
- **Remote branch deletion.** `git push --delete` stays denied, and
  GitHub's *Automatically delete head branches* setting is the owner's to
  turn on.
- **`git reset --hard` and `git clean`.** Both stay denied. They destroy
  uncommitted work on the machine, which is §2 goal 3's other half, whatever
  branch is checked out *(builder's call)*.

## Global Constraints

- `py -3.12` locally; CI pins `python-version: '3.12'`; stdlib only;
  `encoding="utf-8"` on every open and subprocess; Git's `bash.exe` by
  absolute path from Python.
- The floor (spec §5.5) holds in every repository's ruleset on `main`:
  - `deletion` and `non_fast_forward`;
  - `pull_request`, with `required_approving_review_count` 0 and
    `allowed_merge_methods` `["rebase"]`;
  - `required_linear_history`;
  - `required_status_checks`, naming the repository's own required checks;
  - `bypass_actors` empty;
  - enforcement `active`.
- The approval count stays 0. A required approval is a stop the owner must
  answer on every pull request, which is §4's second anti-goal.
- Order (spec §5.5): a repository's client deny on force is relaxed only
  after its ruleset has been read back green. Until then `main` is never
  guarded by the client alone.
- The raw spellings stay denied in `payload/.harness/settings.base.json`:
  `git push --force`, `--force-with-lease`, `-f`, `--delete`,
  `git branch -d`, `-D`, `--force`, and the rest. The relaxation is two
  helpers that hold their own lease and refuse `main` in every spelling.
- A change to the protection surface merges on the `/ship` loop, with no
  approval by the owner (spec §5.1).
- Prose wraps at 80 columns, in British spelling. Commit subjects are
  `chore(harness): …` or `docs(harness): …` in the harness and `/commit`'s
  closed set in backend, with `PR: #<n>` in the trailer block.

## Review Focus

1. **A check that runs on only some pull requests.** If such a check is
   required, the pull requests it skips wait for it forever. Frontend's
   checks are chosen from data, every merged pull request in a window
   (Task 2, Step 1), and a renamed job id fails the harness's own data test
   (Task 1, `test_harness_checks_are_its_job_ids`).
2. **A ruleset weaker than the floor in the data.** A branch that edits
   `rulesets/main.json` must not get a weaker ruleset applied, so the apply
   refuses it before it calls the API (Task 2,
   `test_apply_refuses_data_below_the_floor`).
3. **A ruleset raised beyond the data by hand**, for example one approval
   required. That is a drift too, and the read-back reports it (Task 1,
   `test_compare_reports_raised_approvals`).
4. **`main` spelt otherwise.** `MAIN`, `refs/heads/main`, `origin/main` or
   `heads/main` is refused when handed to `git-branch-drop.sh`, and so is a
   current branch of that name under `git-push-force.sh`, which takes no
   argument (Task 5, `test_main_is_refused_however_it_is_spelled` in each
   helper's suite).
5. **A deny with no row in the destructive table, or a row whose example
   is not refused.** The table must stay the surface it claims to be
   (Task 4, `test_every_base_deny_has_a_row`,
   `test_every_row_is_refused`).

---

## File structure

| Path | Where | Responsibility |
|---|---|---|
| `rulesets/main.json` | harness root | The floor, and each repository's required checks |
| `harness_sync/ruleset.py` | harness | Desired ruleset, the read-back's normal form, the comparison |
| `harness_sync/tests/test_ruleset.py` | harness | The data, the comparison, apply and check over a fake `gh` |
| `.github/workflows/rulesets.yml` | harness root | The read-back of all four repositories, on every pull request, on `main` and daily |
| `payload/.claude/scripts/git-push-force.sh` | harness | A lease-bound force push of the current branch, never `main` |
| `payload/.claude/scripts/git-branch-drop.sh` | harness | Deletes a local branch whose tip a merged pull request landed |
| `payload/.claude/scripts/test_git_push_force.py`, `test_git_branch_drop.py` | harness | Their suites |
| `payload/.claude/scripts/test_destructive_classes.py` | harness | Every base deny has a row, and every row's example is refused |
| `payload/docs/harness-boundaries.md` | harness | *Destructive classes*, new; *Grant inventory and push helpers*, rewritten |

`rulesets/main.json` has this shape, and `harness_sync/ruleset.py` reads
nothing else:

```json
{"name": "main",
 "names": ["main", "main non-writable directly", "protect-main"],
 "floor": {"enforcement": "active", "target": "branch",
           "conditions": {"ref_name": {"include": ["~DEFAULT_BRANCH"], "exclude": []}},
           "bypass_actors": [],
           "rules": ["deletion", "non_fast_forward", "required_linear_history",
                     {"type": "pull_request", "parameters": {"required_approving_review_count": 0,
                      "allowed_merge_methods": ["rebase"]}}]},
 "checks": {"alexander-shamray/blueprint-backend": ["changes", "…"], "…": []}}
```

---

### Task 1: The rulesets as data, and their comparison

**Files:**
- Create: `rulesets/main.json`, `harness_sync/ruleset.py`,
  `harness_sync/tests/test_ruleset.py`

**Interfaces:**
- Produces:
  - `load(path: Path) -> Rulesets`;
  - `desired(data: Rulesets, repo: str) -> dict`, the full ruleset body
    that `POST` and `PUT /repos/{repo}/rulesets` take;
  - `normal_form(api: dict) -> dict`, which keeps only the fields the floor
    and the checks name;
  - `compare(want: dict, have: dict) -> list[str]`, one line per
    difference, empty when they match;
  - `floor_breaches(body: dict) -> list[str]`.

The `checks` entries today, read back on 2026-11-05:
- **backend:** `changes`, `build`, `commits`, `closure-gate`,
  `locality-gate`, `commit-gate`.
- **harness:** `sync-tool`, `commit-gate`, `closure-gate`,
  `harness-drift`, and `payload-suites` on its three runners.
- **admin:** its eleven checks as they stand.
- **frontend:** empty in this task. Task 2 fills it.

The comparison's rules *(builder's call)*:
- every floor rule must be present, with its parameters equal;
- the required checks are equal as sets, in both directions;
- a rule type the floor does not name is admitted, because it is stricter;
- an approval count above 0, or any bypass actor, is a difference.

- [ ] **Step 1: Tests.**
  - `test_floor_names_every_rule_the_spec_requires`: the floor's four
    rules, an empty bypass list, and the condition `~DEFAULT_BRANCH`. Each
    repository's `desired(…)` carries the fifth rule,
    `required_status_checks`, built from its `checks` entry.
  - `test_every_repository_has_a_checks_entry`: the two repositories of
    `targets.json`, backend and the harness, plus frontend and admin, and
    no others.
  - `test_harness_checks_are_its_job_ids`: each harness check is a job id,
    or a job id with its matrix leg, in `.github/workflows/*.yml`. This
    keeps the data and the job id from drifting apart.
  - `test_compare_reports_a_missing_rule`, `…_a_missing_check`,
    `…_an_extra_check`, `…_raised_approvals`, `…_a_bypass_actor`;
    `test_compare_admits_an_extra_rule_type`.
  - `test_normal_form_reads_the_api_shape`: over a fixture recorded from
    `gh api repos/alexander-shamray/blueprint-backend/rulesets/20678668`,
    `compare(normal_form(desired(…, backend)), normal_form(fixture))` is
    empty. The name is not compared, because `normal_form` drops it.
  - `test_floor_breaches_names_each_breach`.
- [ ] **Step 2:** Run them and see them fail. Write the module and the
  data, then run them again and see them pass. Then run
  `py -3.12 -m unittest discover -s harness_sync/tests -t .`.
- [ ] **Step 3:** Commit, open the PR, run the `/ship` loop, and merge on
  green.

### Task 2: Apply and read back, and frontend's checks

**Files:**
- Modify: `harness_sync/__main__.py`, adding the subcommands
  `ruleset-check` and `ruleset-apply`; `harness_sync/ruleset.py`;
  `rulesets/main.json`, adding frontend's checks; `README.md`, where
  *Changes* names `rulesets/main.json` as the owner of the seven checks;
  `.harness/config.json`, the harness's own, whose `settings.allow` gains
  the two subcommands
- Test: `harness_sync/tests/test_ruleset.py`

**Interfaces:**
- Consumes: Task 1's functions.
- Produces:
  - `py -3.12 -m harness_sync ruleset-check [--effective] [<repo>…]`. It
    exits 0 when every named repository, or all of them by default,
    matches, and 1 with one line per difference. Without `--effective` it
    reads `GET /repos/{repo}/rulesets`, finds the ruleset by the lookup
    below, and reads it whole, `bypass_actors` included, which needs
    admin. With
    `--effective` it reads `GET /repos/{repo}/rules/branches/main`, which
    any token can read for a public repository and which carries no bypass
    list. It then compares the rules and checks only.
  - `py -3.12 -m harness_sync ruleset-apply <repo>`. It refuses data that
    has a floor breach. It `PUT`s to the ruleset the lookup finds, and
    `POST`s only when the lookup finds none. Then it runs
    `ruleset-check <repo>` and exits with its code.
  - The repository it acts on comes from the data's keys, never from free
    text. Every `gh` call goes through one argv builder, so the tests can
    swap in a fake.

**The lookup** *(builder's call)*: the one branch ruleset whose name is in
`rulesets/main.json` `"names"`. That field holds `main` and the live names
in full: `main non-writable directly` for backend and the harness, and
`protect-main` for frontend and admin. The ruleset is then read by its id.
More than one match is refused. Because the data's `name` is `main`, the
first `PUT` renames each ruleset.

For frontend and admin the first `PUT` also retargets `ref_name` from
`refs/heads/main` to the floor's `~DEFAULT_BRANCH`. That is the same branch
while `main` is the default. Until it runs, a `ruleset-check` without
`--effective` reports the condition as a difference.

- [ ] **Step 1: Choose frontend's checks from data.** Take the check-run
  names that reported `success` on the head of every one of frontend's
  last 20 merged pull requests, Dependabot's included. A name missing from
  any of them is left out. Record the list and the command in the PR body.
  On 2026-11-05 the last five shared `android`, `audit`, `commit-gate`,
  `commits`, `e2e`, `harness (macos-latest)`, `harness (ubuntu-latest)`,
  `harness (windows-latest)`, `ios-build`, `ios-config` and `web`.
  `issue-form` was absent from three, so it stays out.
- [ ] **Step 2: Tests**, over an injected fake `gh`, as `test_pr.py`'s
  `FakeGh` does:
  - `test_apply_refuses_data_below_the_floor`: no `gh` call is made.
  - `test_apply_puts_to_a_ruleset_found_by_a_live_name`;
    `test_apply_posts_when_no_name_matches`;
    `test_apply_refuses_two_matching_rulesets`.
  - `test_check_reads_bypass_actors`;
    `test_check_effective_ignores_bypass`.
  - `test_check_exits_1_with_one_line_per_difference`.
  - `test_repo_outside_the_data_is_refused`.
- [ ] **Step 3:** Run the tests and see them fail. Implement, then run
  them and see them pass.
- [ ] **Step 4:** Commit, open the PR, run the `/ship` loop, and merge.
- [ ] **Step 5:** From the harness checkout, on that `main`, run
  `ruleset-apply` for each of the four repositories. For backend and the
  harness this is a rename. Admin's is also retargeted to
  `~DEFAULT_BRANCH`. Frontend's is retargeted and also gains its checks.
  Then run
  `ruleset-check` with no arguments and see exit 0. Paste the output into
  the H-row of the task list. If a call is refused, hand it over as a
  checked scratchpad script and a `!` line.

### Task 3: The read-back in CI

**Files:**
- Create: `.github/workflows/rulesets.yml`
- Modify: `rulesets/main.json`, where the harness's checks gain
  `rulesets`; `README.md`, *Changes*

**Interfaces:**
- Consumes: `ruleset-check --effective`.
- Produces: the job id `rulesets`.

The workflow runs on `pull_request`, on `push` to `main`, and on a daily
schedule. It runs `py -3.12 -m harness_sync ruleset-check --effective`
with `GITHUB_TOKEN` (`contents: read`), and adds no secret.
- **On a pull request** it proves that the data the branch carries matches
  the live rulesets.
- **A branch that changes the data** therefore goes red until the change
  is live. Its `ruleset-apply` runs from the branch, once the `/ship` loop
  has no open finding, and before the merge. The job is then rerun. The
  floor refusal is what stops an unmerged branch from weakening `main`
  *(builder's call)*. The README's *Changes* states this order.
- **On the schedule** it catches a ruleset changed by hand. A red
  scheduled run files a `harness`, `high` issue itself, or comments on
  the open one, with the differences as its body. It does so from a job of
  its own limited to `issues: write`, as backend's `main-red.yml` files.
  The read-back job keeps `contents: read` *(builder's call)*.

- [ ] **Step 1:** Write the workflow and leave the data unchanged.
  Commit, open the PR, and wait for the new job to report green on it,
  since the data already matches after Task 2 Step 5. Run the `/ship` loop
  and merge.
- [ ] **Step 2:** On a second branch, add `rulesets` to the harness's
  checks. `test_harness_checks_are_its_job_ids` passes now that the job
  exists. Open the PR and run the `/ship` loop. Then run
  `ruleset-apply alexander-shamray/blueprint-harness` from that branch,
  rerun the `rulesets` job, see it green, and merge. Finally run
  `ruleset-check` with no arguments and see exit 0.

### Task 4: One table of destructive classes, held by a test

**Files:**
- Modify: `payload/docs/harness-boundaries.md`. It gains *Destructive
  classes* after *The guards' threat model*, and its index gains that row.
- Create: `payload/.claude/scripts/test_destructive_classes.py`

**Interfaces:**
- Produces:
  - The table's columns: *Class*, *Example*, *Refused by*, *Test*.
  - *Refused by* takes one of four values: `deny`, `guard-git-argv`,
    `guard-edit-target`, or `ruleset`.
  - The test's `deny_matches(rule: str, command: str) -> bool`, which
    follows Claude Code's prefix semantics: `Bash(x:*)` matches `x` and
    `x …`, and `*` inside a rule matches any run.

Rows cover:
- on the repositories: a push to `main` in each refspec spelling, a force
  push of any kind, a remote branch deletion, a soft reset to a
  remote-tracking ref, and a direct commit to `main` landing through a
  push;
- on the machine: `git reset --hard`, `git clean`, the `git branch` force
  and delete forms, and the control-surface edits `guard-edit-target`
  refuses;
- any other deny in the base, one row each.

A `ruleset` row is the server-side backstop: its test is
`ruleset-check`, which the table names and does not run.

- [ ] **Step 1: Tests.**
  - `test_every_base_deny_has_a_row`: every `deny` entry of the rendered
    root's `.harness/settings.base.json` is matched by some `deny` row's
    example, and a deny with no row fails, naming it.
  - `test_every_row_is_refused`:
    - a `deny` row's example matches a base deny;
    - a `guard-git-argv` row's example is fed to the hook as its JSON
      stdin. The hook exits 0, and its stdout JSON carries
      `permissionDecision` `deny`, which is how both hooks refuse;
    - a `guard-edit-target` row is checked the same way.
  - `test_the_table_is_read`: it parses at least 15 rows, so a renamed
    heading cannot leave the test vacuous.
  - `test_a_planted_unrefused_row_fails`: a synthetic row `git status`
    fails `test_every_row_is_refused`.
- [ ] **Step 2:** Run the tests and see them fail. Write the table, then
  run them and see them pass. Run `payload-suites` as CI does, on the
  rendered root.
- [ ] **Step 3:** Commit, open the PR, run the `/ship` loop, and merge.

### Task 5: The two helpers that relax the policy

**Files:**
- Create: `payload/.claude/scripts/git-push-force.sh`,
  `git-branch-drop.sh`, and their suites
- Modify:
  - `payload/.harness/settings.base.json`: `allow` gains
    `Bash(bash .claude/scripts/git-push-force.sh:*)` and
    `Bash(bash .claude/scripts/git-branch-drop.sh:*)`;
  - `payload/docs/harness-boundaries.md`: *Grant inventory and push
    helpers*, where "the only force push … That deny is untouched" becomes
    the three lease-bound helpers, with the deny still untouched for raw
    spellings; *Destructive classes* gains a row for each helper's refusal
    of `main`;
  - `payload/.claude/commands/pr.md`: *Push the branch first*, where a
    branch wanting a force push to itself uses `git-push-force.sh` and a
    push reaching `main` still stops.

The spec routes force through "the existing push helpers". Those two cover
only a rebase onto `main` and a trailer rewrite. A branch amended or
reworded for any other reason has no route today, so this task adds a
third helper of the same shape *(builder's call)*.

**Interfaces:**
- `git-push-force.sh` takes no argument. It pushes the current branch to
  `origin` with
  `--force-with-lease=refs/heads/<b>:<origin's tip as last fetched>`.
  - It refuses: `main` in any spelling, compared case-folded as
    `git-rebase-onto-main.sh` does; a detached `HEAD`; a branch `origin`
    lacks; and a lease that no longer holds, since git refuses that itself.
  - It also refuses a remote that carries commits this checkout never
    held. A lease is not consent: once another session's commits have been
    fetched, the lease passes. So, after a fresh fetch, `origin`'s tip must
    appear in the local branch's reflog, `git reflog refs/heads/<b>`.
    That is `git-rebase-onto-main.sh`'s `require_remote_carries_nothing_new`
    applied to a rewrite with no fork point.
  - Its exit codes are those `git-rebase-onto-main.sh` uses.
- `git-branch-drop.sh <branch>` deletes the local branch only when all of
  these hold:
  - it is not `main` and not checked out in any worktree;
  - `pr-for-branch.sh <branch>` has a row with state `MERGED`, base
    `main`, and `headRefOid` equal to `git rev-parse <branch>`.

  It then runs `git branch -D`. That is `/ship` step 0's finished
  predicate, so `/ship` step 6 and its teardown may call it where they now
  say "name it in the report and leave it". Wiring `/ship` to it is plan
  4's, because `/ship` is the file plan 4 edits.

The spec's order holds before this task's PR merges: Task 2 Step 5's
`ruleset-check` is green for the harness and backend, the two targets its
sync reaches. Re-run it and quote it in the PR body.

- [ ] **Step 1: Tests**, over real repositories with a bare origin, as
  `test_git_rebase_onto_main.py` does:
  - `test_main_is_refused_however_it_is_spelled` (each suite);
  - `test_there_is_exactly_one_push_and_it_leases` (push-force);
  - `test_a_moved_remote_tip_refuses_the_push` (push-force);
  - `test_a_branch_origin_lacks_is_refused` (push-force);
  - `test_remote_commits_this_checkout_never_held_are_refused`
    (push-force: a second clone pushes a commit, this clone fetches it and
    amends its own tip, and the push is refused);
  - `test_an_unmerged_branch_is_kept`, `test_a_tip_after_the_merge_is_kept`,
    `test_a_branch_checked_out_elsewhere_is_kept` and
    `test_a_landed_branch_is_dropped` (branch-drop, over a fake
    `pr-for-branch.sh`);
  - `test_destructive_classes` stays green with the new rows.
- [ ] **Step 2:** Run the tests and see them fail. Write both helpers, the
  grants and the prose, then run the tests and see them pass. Run
  `payload-suites` on the rendered root.
- [ ] **Step 3:** Commit, open the PR, run the `/ship` loop, and merge.
  Merge the self-sync it opens after one review round, then relaunch a
  harness session and check that `bash .claude/scripts/git-push-force.sh`
  on a scratch branch runs without a prompt.

### Task 6: Backend takes the relaxation

- [ ] **Step 1:** Take the sync PR that Task 5's merge opened, as plan 2
  Task 7 did:
  - run backend's local suites the sync touches first, among them the
    backend-local `test_harness_denies.py`;
  - fix any failure in the payload, never here;
  - run one `branch-reviewer` round on the target-specific part;
  - merge on green.
  Backend's local text needs no change. `docs/harness-local.md` never
  states the force policy. `ship.md`'s "`git branch -d` is denied" stays
  true, because the raw deny stays, and plan 4 wires the helper there.
- [ ] **Step 2:** After the next two backend `/ship` sessions, add their
  stop count to the harness's `docs/baseline-2026-11.md` as *After plan 3*,
  measured as *After plan 2* was. A rise is a harness defect, filed there.
