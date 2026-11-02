# Shared harness, plan 1 — the mechanism and backend's first sync

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.
>
> **Execution method:** subagent-driven, chosen by the owner on 2026-11-02.
> Tasks 1–8 run from a session in `C:\dev\ashamray\blueprint-harness`
> (Task 1 from `C:\dev\ashamray`, since the repository does not exist yet);
> Task 9's backend pull request runs from a backend session.

**Goal:** Stand up `blueprint-harness` with backend's harness as its payload,
a sync tool, a drift gate and a sync workflow, and land the first sync pull
request in `blueprint-backend`.

**Architecture:** The harness repository keeps canonical files under
`payload/`, laid out at target paths. A stdlib-only Python package,
`harness_sync`, renders a target's `.claude/settings.json` from a base plus the
target's `.harness/config.json`, copies the managed files, and writes
`.harness/manifest.json`; a vendored drift gate in each target re-checks the
manifest in CI. A workflow in the harness repository opens one sync pull
request per target on every merge to its `main`.

**Tech Stack:** Python 3.12 stdlib only, `unittest`, Git Bash, `gh`, GitHub
Actions.

**Spec:** `docs/superpowers/specs/2026-11-02-shared-harness-design.md` in
`blueprint-backend`. Read it before any task; §-references below are its.

**Scope.** This is the first of six plans. Out of scope here, each its own
later plan: splitting `CLAUDE.md` and `docs/harness-boundaries.md` and moving
`token-plan.md` rules and memory lessons (spec §5.2 docs, §5.6, §5.7); main
rulesets and the force-push relaxation (§5.5); `/dispatch` taking sync pull
requests up (§5.3, first bullet); frontend's sync; admin's sync. Until the
`/dispatch` plan lands, a sync pull request is reviewed and merged by a
session through `/ship`'s review loop limited to one round.

## Global Constraints

- `py -3.12`, never `python`, locally; CI pins `python-version: '3.12'`.
- Stdlib only in `harness_sync` and every gate, as in backend's
  `.github/licence-gate` terms; no YAML — config and manifest are JSON.
- Every file open names `encoding="utf-8"`; every subprocess passes
  `encoding="utf-8"`, never bare `text=True` (cp1252 drops em dashes here).
- Git's `bash.exe` by absolute path from Python, never a bare `bash`.
- Hashes are SHA-256 over content with `\r\n` replaced by `\n`; the sync
  writes payload bytes as stored and never rewrites a local file's line
  endings.
- A rendered permission rule never uses `Write(`; a file rule is `Edit(...)`.
- `.claude/settings.json` in a target is written whole and last in any
  change that touches it.
- Prose wraps at 80 columns, code at 120, British spelling; comments say why
  and cite an owner (backend's `docs/style-guide.md`).
- Commit subjects keep `/commit`'s form, types from its closed set — harness
  work is `chore(harness): …` or `docs(harness): …`; every commit on a pull
  request carries `PR: #<n>` in its trailer block (commit gate).
- Backend's import pin: the payload is taken from one named backend commit
  (Task 5). Any backend change under a managed path merged after it is
  ported into the payload before Task 9's sync opens.
- Each repository's `main` stays green at every task's end.

## Review Focus

1. A target config whose `allow` holds an entry a base `deny` lists, or whose
   `deny`/`hooks` tries to drop a base entry — rendering refuses with the
   entry named, rather than producing a weaker file (Task 3).
2. A file removed from the payload — the next sync deletes it from the
   target; a path the target lists under `local` is never deleted or
   overwritten (Task 4).
3. A Windows checkout with CRLF copies of managed files — the drift gate
   passes on identical content and fails on a one-character change (Task 6).
4. A second merge to the harness `main` while an older sync pull request is
   still open — the new sync closes the old one and says which superseded
   it, so `/ship` never merges a stale payload (Task 8).
5. A sync with nothing to change — no branch, no pull request, exit 0 with a
   line saying the target is current (Task 8).

---

## File structure (harness repository)

| Path | Responsibility |
|---|---|
| `payload/**` | Canonical managed files at target paths |
| `payload/.harness/settings.base.json` | The base settings every target renders from |
| `payload/.harness/render.py` | Vendored renderer (a copy of `harness_sync/render.py`, kept identical by a test) |
| `payload/.github/harness-drift/drift_gate.py`, `test_drift_gate.py`, `README.md` | The drift gate a target runs |
| `payload/.github/workflows/harness-drift.yml` | Runs the drift gate on every target pull request |
| `harness_sync/config.py` | Load and validate `.harness/config.json` |
| `harness_sync/render.py` | Base + config → settings dict |
| `harness_sync/manifest.py` | Hashing, manifest read/write |
| `harness_sync/apply.py` | Payload → target tree |
| `harness_sync/pr.py` | Sync branch, commit, pull request, superseding |
| `harness_sync/__main__.py` | CLI: `apply`, `check`, `open-pr` |
| `harness_sync/tests/` | Unit tests per module |
| `measure/stops.py`, `measure/test_stops.py` | Stop counter over Claude Code transcripts |
| `targets.json` | The sync targets |
| `.harness/config.json` | The harness repository's own target config |
| `.github/workflows/ci.yml` | Sync-tool tests, and the payload's suites run in a rendered worktree |
| `.github/workflows/sync.yml` | One sync pull request per target on merge |
| `docs/baseline-2026-11.md` | §7 step 0's figures |

Config schema, used from Task 3 on (`.harness/config.json`):

```json
{
  "repo": "alexander-shamray/blueprint-backend",
  "task_list": ".todo/blueprint-backend.md",
  "settings": {"env": {}, "allow": [], "deny": [], "hooks": {}},
  "local": ["glob", "..."],
  "exclude": ["payload path glob", "..."]
}
```

Manifest schema (`.harness/manifest.json`):

```json
{"harness_commit": "<40-hex>", "files": {"<path>": "<sha256-hex>"}}
```

---

### Task 1: The harness repository and its protected `main`

**Files:**
- Create: `README.md`, `.gitignore`, `.gitattributes`, `targets.json`,
  `.github/workflows/ci.yml`, `harness_sync/__init__.py`,
  `harness_sync/tests/__init__.py`, `harness_sync/tests/test_smoke.py`

**Interfaces:**
- Produces: `targets.json` =
  `[{"repo": "alexander-shamray/blueprint-backend"}]` (frontend and admin are
  added by their own plans); CI job `sync-tool` running
  `python -m unittest discover -s harness_sync/tests -t .`.

- [ ] **Step 1:** `gh repo create alexander-shamray/blueprint-harness --public`
  (public, as the three targets are), clone to
  `C:\dev\ashamray\blueprint-harness`.
- [ ] **Step 2:** Write `test_smoke.py::test_package_imports` asserting
  `import harness_sync` succeeds; add `ci.yml` with the `sync-tool` job.
- [ ] **Step 3:** `.gitattributes` copies backend's text/eol rules for `*.py`,
  `*.sh`, `*.md`, `*.json`, `*.yml` (LF for `*.sh`).
- [ ] **Step 4:** Push the initial commit to `main` (the only direct push the
  repository ever takes — it has no `main` before it).
- [ ] **Step 5:** Create a ruleset on `main`: no deletion, no non-fast-forward,
  pull request required, required check `sync-tool`, no bypass actors. Read it
  back with `gh api repos/alexander-shamray/blueprint-harness/rulesets` and
  check all four rules are present.
- [ ] **Step 6:** Verify: a throwaway branch's PR shows `sync-tool` green.

### Task 2: The stop counter and the baseline

**Files:**
- Create: `measure/stops.py`, `measure/test_stops.py`,
  `docs/baseline-2026-11.md`

**Interfaces:**
- Produces: `count_stops(transcript: pathlib.Path) -> dict[str, int]` with
  keys `ask_user`, `action_required`, `permission_refused`, `user_turns`; CLI
  `py -3.12 measure/stops.py --project <dir> --since YYYY-MM-DD --command ship`
  printing one JSON line per session and a median line.

A stop is §4's: an `AskUserQuestion` tool use; an assistant message whose
last line starts `⚠️ ACTION REQUIRED`; a tool result that is an error whose
text is a permission refusal. `user_turns` counts genuine user messages
(not tool results, not hook or system injections) after the first.

- [ ] **Step 1:** Open one real `.jsonl` under
  `~/.claude/projects/C--dev-ashamray-blueprint-backend/` and record, in the
  test module's docstring, the field shapes of each of the four events (a
  permission refusal and a hook refusal both carry `is_error: true`; only the
  text tells them apart).
- [ ] **Step 2:** Write `test_stops.py` with one synthetic transcript per
  event: `test_ask_user_counted`, `test_action_required_counted`,
  `test_permission_refusal_counted`,
  `test_hook_refusal_not_a_permission_stop`,
  `test_tool_result_is_not_a_user_turn`,
  `test_command_filter_matches_first_user_message` (a session is a `/ship`
  session when its first user message carries `<command-name>/ship`).
- [ ] **Step 3:** Run, see them fail; implement; run, see them pass.
- [ ] **Step 4:** Run the CLI for each of the three project directories and
  their worktree directories since 2026-10-19 for `ship` and for
  `bug-sweep`; write the medians and session counts into
  `docs/baseline-2026-11.md`, with the command lines used.
- [ ] **Step 5:** Commit on a branch, PR, merge on green.

### Task 3: Config and the settings renderer

**Files:**
- Create: `harness_sync/config.py`, `harness_sync/render.py`,
  `harness_sync/tests/test_config.py`, `harness_sync/tests/test_render.py`

**Interfaces:**
- Produces: `load_config(path: Path) -> Config` (frozen dataclass: `repo`,
  `task_list`, `settings: dict`, `local: tuple[str, ...]`,
  `exclude: tuple[str, ...]`); `ConfigError(Exception)`;
  `render_settings(base: dict, cfg: Config) -> dict`;
  `dump_settings(settings: dict) -> str` (two-space indent, key order kept,
  trailing newline — the form backend's file has today); `RenderError`.

Rendering: `$schema` and `env` from the base, then config `env` keys added
(a key the base sets cannot be overridden); `allow` = base then config, no
duplicates; `deny` = base then config; `hooks` per event = base entries then
config entries.

- [ ] **Step 1: Tests.**
  - `test_config_rejects_unknown_key` — `{"repo": …, "extra": 1}` →
    `ConfigError` naming `extra`.
  - `test_allow_appended_after_base`.
  - `test_allow_entry_matching_base_deny_refused` — base deny
    `Bash(git push --force:*)`, config allow `Bash(git push --force:*)` →
    `RenderError` naming the entry.
  - `test_env_override_of_base_key_refused`.
  - `test_write_rule_refused` — config deny `Write(.claude/**)` →
    `RenderError` (a `Write(` rule stops Claude Code starting).
  - `test_hooks_config_entries_follow_base_entries` per event.
  - `test_dump_matches_backend_form` — dumping backend's current
    `.claude/settings.json` (fixture copy) after `json.loads` reproduces it
    byte-for-byte, CRLF-normalised.
- [ ] **Step 2:** Run, see them fail; implement; run, see them pass.
- [ ] **Step 3:** Commit, PR, merge on green.

### Task 4: Manifest and apply

**Files:**
- Create: `harness_sync/manifest.py`, `harness_sync/apply.py`,
  `harness_sync/__main__.py`, `harness_sync/tests/test_manifest.py`,
  `harness_sync/tests/test_apply.py`

**Interfaces:**
- Consumes: `load_config`, `render_settings`, `dump_settings` (Task 3).
- Produces: `file_hash(data: bytes) -> str`;
  `Manifest(harness_commit: str, files: dict[str, str])` with
  `Manifest.read(path) -> Manifest | None` and `write(path)`;
  `apply(payload: Path, target: Path, harness_commit: str) -> ApplyResult`
  (`written`, `deleted`, `unchanged`: sorted path lists); CLI
  `py -3.12 -m harness_sync apply --payload <dir> --target <dir>
  --commit <sha>`.

`apply` reads `<target>/.harness/config.json`; the managed set is every file
under `payload/` minus `exclude` globs — `.harness/settings.base.json` and
`.harness/render.py` included, since the drift gate re-renders from them;
paths matching
`local` are skipped with a refusal if the payload also has them and the
target file differs (a local path shadowing a managed one is a config error
to surface, not a silent fork). `.claude/settings.json` is written last; the
manifest records it with its rendered hash. Files in the old manifest and
absent from the new managed set are deleted.

- [ ] **Step 1: Tests** (each over `tempfile.TemporaryDirectory` trees):
  - `test_hash_ignores_crlf` — `b"a\r\nb\n"` and `b"a\nb\n"` hash equal.
  - `test_apply_copies_managed_and_writes_manifest`.
  - `test_apply_deletes_file_dropped_from_payload` (Review Focus 2).
  - `test_apply_never_touches_local_path` (Review Focus 2).
  - `test_local_shadowing_managed_path_refused`.
  - `test_excluded_payload_path_not_copied`.
  - `test_settings_written_last` — record write order through an injected
    writer; `.claude/settings.json` is the final write.
  - `test_second_apply_is_unchanged` — `written == deleted == []`.
- [ ] **Step 2:** Run, fail; implement; run, pass.
- [ ] **Step 3:** Commit, PR, merge on green.

### Task 5: Import backend's harness as the payload

**Files:**
- Create: `payload/**` from backend at a pinned commit;
  `payload/.harness/settings.base.json`; `payload/.harness/render.py`;
  `harness_sync/tests/test_vendored_render.py`; `IMPORT.md` (pin and the
  classification table)
- Modify: `.github/workflows/ci.yml` (job `payload-suites`)

**Interfaces:**
- Consumes: `apply` (Task 4).
- Produces: the payload; backend's draft config at
  `targets/blueprint-backend.config.json` (moved into backend by Task 9);
  CI job `payload-suites`.

The import takes, from backend `main` at a commit recorded in `IMPORT.md`:
`.claude/agents/`, `.claude/commands/`, `.claude/hooks/`, `.claude/scripts/`,
`.claude/skills/codebase-index/`, `docs/commands/`,
`docs/harness-boundaries.md`, and the gates `.github/commit-gate/`,
`.github/closure-gate/`, `.github/comment-gate/`, `.github/issue-form-gate/`,
`.github/plan-size-gate/`, `.github/output-gate/`, `.github/workflow-lint/`,
`.github/ISSUE_TEMPLATE/`, with each gate's workflow under
`.github/workflows/`. Not imported, backend-local by spec §3 and §8:
`dotnet-test.sh`, `mutation-run.sh`, `mutation_survivors.py`,
`test_mutation_survivors.py`, `stryker-config.json`, the commands and
runbooks `validate-blueprint`, `new-adr`, `new-chapter`, `check-links`,
`mutation-sweep`, the domain gates, `locality-gate/classes.yml`, `ci.yml`.

- [ ] **Step 1:** Classify every candidate file into payload or
  backend-local in `IMPORT.md`, one row each with the reason; a test module
  goes with the code it tests, and one that asserts backend-only values
  (dotnet denies, `Platform.slnx`, blueprint chapters) is either made to
  read them from `.harness/config.json` or stays local — the row says which.
- [ ] **Step 2:** Split backend's `settings.json` into
  `settings.base.json` and the draft backend config's `settings` additions.
  Test `test_backend_render_equivalent`: rendering base + draft config gives
  backend's current file's `allow`, `deny` and `hooks` as the same multisets
  and the same `env`.
- [ ] **Step 3:** Replace the hard-coded repository slug and task-list path in
  payload files (18 files name `blueprint-backend`, `.todo/` or a stack
  command today) with reads of `.harness/config.json` keys `repo` and
  `task_list`; commands say "the task list named by `.harness/config.json`
  `task_list`".
- [ ] **Step 4:** Copy `harness_sync/render.py` to `payload/.harness/render.py`;
  `test_vendored_render_identical` asserts the two files' bytes match.
- [ ] **Step 5:** Add CI job `payload-suites`: `git worktree add` a scratch
  copy of the pull request head, `py -m harness_sync apply` the payload into
  it with `targets/blueprint-backend.config.json` as its config, then run
  `python -m unittest discover -s .claude/scripts` and each imported gate's
  suite from inside it. Runs on `ubuntu-latest` and `windows-latest`, as
  backend's matrix does for the guards.
- [ ] **Step 6:** Run `payload-suites` locally the same way; every suite
  backend's CI ran green at the pin is green here (691 in `review-helpers`
  at the time of writing, less what `IMPORT.md` left local).
- [ ] **Step 7:** Commit, PR, merge on green.

### Task 6: The drift gate

**Files:**
- Create: `payload/.github/harness-drift/drift_gate.py`,
  `payload/.github/harness-drift/test_drift_gate.py`,
  `payload/.github/harness-drift/README.md`,
  `payload/.github/workflows/harness-drift.yml`

**Interfaces:**
- Consumes: the manifest format (Task 4), `payload/.harness/render.py`
  (Task 5).
- Produces: `check(root: Path) -> list[str]` (problems, empty when clean);
  CLI `py -3.12 .github/harness-drift/drift_gate.py` exiting 1 with one line
  per problem.

Problems: a manifest path missing; a manifest path whose hash differs; a
file under a managed directory (`.claude/agents`, `.claude/commands`,
`.claude/hooks`, `.claude/scripts`, `.claude/skills`, `docs/commands`,
`.github/harness-drift`) neither in the manifest nor matching `local`;
`.claude/settings.json` not equal to `render.py`'s output for
`.harness/settings.base.json` plus `.harness/config.json`.

- [ ] **Step 1: Tests.** `test_clean_tree_passes`;
  `test_crlf_copy_passes` and `test_one_char_change_fails` (Review Focus 3);
  `test_missing_managed_file_fails`; `test_unlisted_file_in_managed_dir_fails`;
  `test_local_file_in_managed_dir_passes`;
  `test_settings_edited_by_hand_fails`;
  `test_gate_reads_the_shipped_manifest` — run against the repository's own
  rendered root, so the gate is observed looking at the file CI hands it.
- [ ] **Step 2:** Run, fail; implement; run, pass.
- [ ] **Step 3:** `harness-drift.yml` runs the gate on every pull request with
  no path filter, judging with the base commit's copy of the gate as
  backend's commit gate does.
- [ ] **Step 4:** Commit, PR, merge on green.

### Task 7: The harness repository syncs itself

**Files:**
- Create: `.harness/config.json`, and the rendered root (`.claude/**`,
  `docs/commands/**`, `.github/<managed>`, `.harness/manifest.json`)

**Interfaces:**
- Consumes: `apply` (Task 4), payload (Tasks 5–6).

- [ ] **Step 1:** Write the harness repository's config: `repo`
  `alexander-shamray/blueprint-harness`, its own `task_list`
  (`.todo/blueprint-harness.md`), no settings additions beyond what its own
  CI needs, `local: ["payload/**", "harness_sync/**", "measure/**"]`.
- [ ] **Step 2:** On a branch, `py -3.12 -m harness_sync apply --payload payload
  --target . --commit <main sha>`; commit as
  `chore(harness): sync to blueprint-harness <short sha>`.
- [ ] **Step 3:** Open the PR; its own drift gate, commit gate and closure
  gate run against it; merge on green. From here on, sessions in the harness
  repository run under its own harness — relaunch the session to load it.
- [ ] **Step 4:** Add the required checks the new gates provide to the
  ruleset and read the ruleset back.

### Task 8: The sync workflow

**Files:**
- Create: `harness_sync/pr.py`, `harness_sync/tests/test_pr.py`,
  `.github/workflows/sync.yml`

**Interfaces:**
- Consumes: `apply` (Task 4).
- Produces: `open_sync_pr(target_checkout: Path, harness_commit: str,
  gh: GhRunner) -> SyncOutcome` (`state`: `current` | `opened`, `pr`,
  `superseded: list[int]`); `GhRunner` a protocol wrapping
  `run(args: list[str]) -> str`, faked in tests.

Branch `chore/harness-sync-<short sha>`; commit subject
`chore(harness): sync to blueprint-harness <short sha>`; body from a
template that carries backend's house form — `| Class | D |`,
`| Touch set |` listing the changed managed paths, `| Closes | — |` — and
the harness commit link. After `gh pr create`, the commit is amended with
`PR: #<n>` (backend's `git-trailer-pr.sh` shape) and force-pushed to the
sync branch. Any other open PR whose head starts `chore/harness-sync-` is
closed with a comment naming the new one.

- [ ] **Step 1: Tests.** `test_nothing_to_sync_opens_nothing` (Review
  Focus 5); `test_opened_pr_body_has_class_touch_set_closes`;
  `test_trailer_added_after_create`; `test_older_sync_pr_closed`
  (Review Focus 4); `test_touch_set_lists_only_changed_paths`.
- [ ] **Step 2:** Run, fail; implement; run, pass.
- [ ] **Step 3:** `sync.yml`: on push to `main` touching `payload/**` or
  `harness_sync/**`, and `workflow_dispatch`; matrix over `targets.json`;
  checks out the target with secret `HARNESS_SYNC_TOKEN`, runs
  `python -m harness_sync open-pr`.
- [ ] **Step 4 (owner):** Create a fine-grained token with Contents and Pull
  requests read-write on the three target repositories and store it as
  `HARNESS_SYNC_TOKEN` in the harness repository. Hand this over as one
  `⚠️ ACTION REQUIRED` with the exact `gh secret set` line; the rest of the
  task does not wait on it.
- [ ] **Step 5:** Commit, PR, merge on green.

### Task 9: Backend's first sync

Two pull requests in `blueprint-backend`, in order.

**Files (pull request 1, backend, Class D):**
- Create: `.harness/config.json` (from `targets/blueprint-backend.config.json`)
- Modify: `docs/harness-boundaries.md` — a section *Managed files* stating
  that paths in `.harness/manifest.json` are owned by `blueprint-harness`,
  changed there and never here, and that the drift gate holds it;
  `docs/change-locality.md` §6, one working rule citing that section;
  `.github/locality-gate/classes.yml`, `'.harness/**'` added to Class D so
  a sync pull request is inside its declared class (this pull request edits
  the map, so run `locality_gate.py` on it by hand — CI judges with the
  base copy). Check `.gitignore` excludes nothing under `.harness/`.

**Files (pull request 2, opened by `sync.yml`):** the managed paths,
`.harness/manifest.json`, `.harness/settings.base.json`,
`.harness/render.py`, `.github/harness-drift/**`,
`.github/workflows/harness-drift.yml`, `.claude/settings.json`.

- [ ] **Step 1:** Port into the payload every backend change under a managed
  path merged since Task 5's pin (`git log <pin>..origin/main -- <paths>`);
  merge in the harness repository first.
- [ ] **Step 2:** Pull request 1 through `/ship`; merge on green.
- [ ] **Step 3:** Run `sync.yml` by `workflow_dispatch`. Expected diff:
  the manifest, the vendored renderer and base, the drift gate, the
  repository-slug and task-list reads from Task 5 Step 3, and
  `settings.json` reordered only — no other content change. Any other hunk
  is a payload defect: fix it in the harness repository and re-run, never in
  backend.
- [ ] **Step 4:** One `branch-reviewer` round on the backend-specific part of
  the diff (config, rendered `settings.json`, the docs section), the diff
  written under `artifacts/review/`. Merge on green and a clean round.
- [ ] **Step 5:** Relaunch a backend session; run `measure/stops.py` over its
  next `/ship` and compare with `docs/baseline-2026-11.md`. A rise is a
  defect filed in the harness repository (spec §4).
- [ ] **Step 6:** Update the task list: the backend row, and a row in the
  harness repository's own task list for plans 2–6.
