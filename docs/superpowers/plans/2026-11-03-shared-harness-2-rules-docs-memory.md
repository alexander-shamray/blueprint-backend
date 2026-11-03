# Shared harness, plan 2 — the rules, the docs split and the memory triage

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.
>
> **Where each task runs:** Tasks 1–5 from a session in
> `C:\dev\ashamray\blueprint-harness`; Tasks 6–9 from a backend session.
> Relaunch a session after any sync it depends on: permissions and
> `CLAUDE.md` are read at start.

**Goal:** Write the agent's working rules once, in the harness, as a short
always-loaded file that every target's `CLAUDE.md` imports and a few
on-demand documents, and turn the project memories into those rules.

**Architecture:** The payload gains `.harness/agent-rules.md`, imported by
`@.harness/agent-rules.md` from each target's own `CLAUDE.md`, and three
managed documents under `docs/harness/`. Each target gains one local file,
`docs/harness-local.md`, which names its own documents, `/ship` checks and
residuals, so managed text cites that file and never a backend-only one.
The move runs expand-then-contract: the new home lands and syncs before the
old copy is cut, so a session between the two reads a rule twice, never
zero times.

**Tech Stack:** Markdown, Python 3.12 stdlib `unittest`, Git Bash, `gh`.

**Spec:** `docs/superpowers/specs/2026-11-02-shared-harness-design.md` in
`blueprint-backend`, §5.2 (the `CLAUDE.md` and docs bullets), §5.6, §5.7 and
§6. Plan 1 (`docs/superpowers/plans/2026-11-02-shared-harness-1-mechanism.md`)
built the sync this plan rides on.

**Scope.** Out of scope, each a later plan: stack facts in the payload
(`IMPORT.md` *Stack mentions left verbatim*, the sweeps' `Platform.slnx`
probe, the stack's allow entries now in `settings.base.json`), which move to
config when a second stack arrives (plan 5); rulesets and force-push
(plan 3); `/dispatch` taking sync pull requests (plan 4); frontend and admin
syncs (plans 5, 6), which is also when their memories are removed. Until
plan 4, a sync pull request lands by a session through `/ship`'s review loop
limited to one round, as plan 1's did.

## Global Constraints

- `py -3.12` locally; CI pins `python-version: '3.12'`; stdlib only;
  `encoding="utf-8"` on every open and subprocess.
- Claude Code's import rules (code.claude.com/docs/en/memory, *Import
  additional files*): a path resolves against the importing file; an import
  inside the working directory raises no approval dialog; imports recurse at
  most four hops; code spans and fenced blocks are not parsed; imported
  bytes load at session start and cost context like `CLAUDE.md` itself. What
  a missing import does is undocumented, so a `CLAUDE.md` imports a file only
  once that file is on the repository's `main`.
- The always-loaded managed part — `.harness/agent-rules.md` and everything
  it imports — is at most **8526 bytes**, LF-normalised: backend's
  `CLAUDE.md` at `b57b12d24` (spec §5.6).
- One owner per fact (spec §6): a rule's text lives in one managed file;
  every other mention cites it by path and section, never restates it, and
  no measured number is copied out of a target's measurement document.
- A rule leaves its old home only in the pull request that lands after its
  new home is on that repository's `main` (expand, then contract). The
  window between states which copy is the owner.
- A memory file is moved to an archive directory, never deleted, and only
  after the rule it became is on `main` in that repository (spec §5.7).
- Prose wraps at 80 columns, British spelling; commit subjects
  `chore(harness): …` / `docs(harness): …` in the harness repository and
  `/commit`'s closed set in backend; `PR: #<n>` in the trailer block.

## Review Focus

1. An `@path` inside a code span or fenced block in `agent-rules.md` — not an
   import, so the budget does not count it and the resolver does not chase
   it (Task 1, `test_import_in_code_span_ignored`).
2. An import chain deeper than four hops — Claude Code drops the tail
   silently, so the budget test refuses it rather than counting bytes no
   session loads (Task 1, `test_import_depth_over_four_refused`).
3. A citation of a heading a later edit renames — `agent-rules.md`,
   *Hard rules* — resolves to nothing while every path still exists
   (Task 4, `test_cited_section_exists`).
4. A CRLF checkout of `agent-rules.md` — the budget reads the LF form, so a
   Windows checkout neither fails nor passes a file CI would judge otherwise
   (Task 1, `test_budget_reads_lf_form`).
5. A memory removed whose rule never landed — the archive step checks each
   memory's destination text on `main` before it moves the file (Task 9,
   Step 2).

---

## File structure

| Path | Where | Responsibility |
|---|---|---|
| `payload/.harness/agent-rules.md` | harness | The always-loaded working rules |
| `payload/docs/harness/working-rules.md` | harness | Commits, PRs, review, CI, issues, task list; the review bar |
| `payload/docs/harness/token-rules.md` | harness | `docs/token-plan.md`'s measures as rules, and step 7d's model table |
| `payload/docs/harness/pitfalls.md` | harness | Shell, git, machine and auto-mode traps, by topic |
| `harness_sync/tests/test_always_loaded.py` | harness | The budget gate |
| `harness_sync/tests/test_payload_references.py` | harness | Every citation resolves in any target |
| `payload/.claude/scripts/test_python_hygiene.py` | harness | No `text=True` without `encoding=`, no bare `bash` |
| `docs/harness-local.md` | each target, local | *Documents*, *Ship checks*, *Residuals* |
| `CLAUDE.md` | each target, local | The product; one `@.harness/agent-rules.md` line |

`docs/harness-local.md`'s contract — its three headings and what each holds —
is owned by the payload's `docs/harness-boundaries.md` *Managed files*
(Task 4). The headings are fixed, because managed text cites them:

- *Documents*: one row per role — change contract, style guide, testing
  document, product specification, roadmap — with the target's path or
  `none`.
- *Ship checks*: what `/ship` step 2 runs in this repository, and when.
- *Residuals*: what this target's config additions grant or refuse beyond
  the base, as `docs/harness-boundaries.md` states residuals.

---

### Task 1: The always-loaded rules file and its budget

**Files:**
- Create: `payload/.harness/agent-rules.md`,
  `harness_sync/tests/test_always_loaded.py`

**Interfaces:**
- Produces: the headings later tasks cite, exactly — *Hard rules*,
  *Working in a target*, *Subagents, review rounds and plans*,
  *Handing over to the owner*, *Where the detail is*;
  `imports(path: Path, root: Path) -> list[Path]` and
  `loaded_bytes(path: Path, root: Path) -> int` in the test module, reused
  by Task 4.

Content, taken from backend's `CLAUDE.md` at `b57b12d24` and reworded to
hold in any target:

- *Hard rules*: `py -3.12`; `Edit(...)`, never `Write(...)`; the
  `settings.json` self-lock; a gate with a suite is tested then run; a gate
  that silently stops covering the newest surface is the most-repeated
  failure, defended by a test whose subject is the surface.
- *Working in a target*: uncommitted work belongs in the PR; the task list
  is the path `.harness/config.json` names under `task_list`, updated at the
  moments backend's `CLAUDE.md` lists; a managed file (one in
  `.harness/manifest.json`) is changed in `blueprint-harness`.
- *Subagents, review rounds and plans*: backend's section of that name,
  citing `docs/harness/token-rules.md` where it cites `docs/token-plan.md`.
- *Handing over to the owner*: the rows Table M sends here.
- *Where the detail is*: one line per managed document and
  `docs/harness-local.md`, saying when to read it.

What stays in backend's `CLAUDE.md` (Task 8): what the repository is, the
one rule, its commands, the .NET hard rules (container tests, exact pins,
analysers, §4.2), style, and the precedence list.

- [ ] **Step 1: Tests.**
  - `test_budget_holds` — `loaded_bytes(payload/.harness/agent-rules.md)`
    ≤ 8526.
  - `test_budget_counts_imports` — a 100-byte file importing a 9000-byte
    file fails the budget.
  - `test_import_in_code_span_ignored` — `` `@big.md` `` and a fenced
    `@big.md` add no bytes and no resolution error.
  - `test_import_depth_over_four_refused` — a chain of five hops raises,
    naming the fifth file.
  - `test_imports_resolve_inside_payload` — every import of the real file
    resolves under `payload/`.
  - `test_budget_reads_lf_form` — a CRLF copy counts as its LF form.
  - `test_agent_rules_is_managed` — no `exclude` glob in
    `targets/*.config.json` or `.harness/config.json` matches it.
- [ ] **Step 2:** Run, see them fail; write the file and the helpers; run,
  see them pass. Report the byte count in the PR body.
- [ ] **Step 3:** Commit, PR, `/ship` loop, merge on green.

### Task 2: Working rules and token rules

**Files:**
- Create: `payload/docs/harness/working-rules.md`,
  `payload/docs/harness/token-rules.md`
- Modify: `payload/.github/harness-drift/drift_gate.py` (managed
  directories gain `docs/harness`), its `test_drift_gate.py`

**Interfaces:**
- Produces: working-rules headings *Commits*, *Pull requests*, *Review*,
  *What counts as a finding*, *CI*, *Tests and gates*,
  *Issues and the task list*, *Managed files*; token-rules headings
  *What every session loads*, *Briefing a subagent*, *Reviewers*,
  *Which model a subagent runs on*, *Waits*.

working-rules takes backend's `docs/change-locality.md` §6 *Working rules*
bullets led **Commit messages**, **Every commit carries `PR: #<n>`**,
**A branch is updated for a conflict**, **A red `main` is reverted first**,
**A Dependabot pull request is the one exception**, **The issue vocabulary**,
**A reply is not a resolution**, **A round's touch set**,
**A CodeQL alert**, **A fix to a script under `.claude/`**,
**From the first sync, a managed file** and **Uncommitted work**; the
**task list** bullet comes reworded to the config's `task_list`. **Read
before you edit** stays in backend: it is about the blueprint's claims. Any
moved bullet that names a class, the locality gate or another backend-only
fact cites `docs/harness-local.md` *Documents* (the change contract) for it.
*What counts as a finding* is backend's `.claude/commands/review-branch.md`
section of that name, which `branch-reviewer` reads today. Then the rows of
Table M sent here.

token-rules states as rules, each with its one-line reason: spec §5.6's five
measures, from `docs/token-plan.md` steps 2, 3, 4, 6, 7 and 7d, with step
7d's table of tasks and models; and Table M's row. It cites "the target's
own measurements" for every figure and copies none.

- [ ] **Step 1:** `test_unlisted_file_in_docs_harness_fails` and
  `test_docs_harness_file_in_manifest_passes` in `test_drift_gate.py`; run,
  fail; add the directory; run, pass.
- [ ] **Step 2:** Write both documents. Check each moved bullet against its
  source by reading the two side by side; a rule may be shortened, never
  changed in meaning.
- [ ] **Step 3:** Commit, PR, `/ship` loop, merge on green.

### Task 3: Pitfalls, refusals that teach, and a hygiene test

**Files:**
- Create: `payload/docs/harness/pitfalls.md`,
  `payload/.claude/scripts/test_python_hygiene.py`
- Modify: the payload guard or helper each `G` row of Table M names, and its
  suite

**Interfaces:**
- Produces: pitfalls headings *Shell and encoding*, *Git and worktrees*,
  *Machine and network*, *Auto mode and probes*.

A lesson an existing guard or helper already enforces becomes that refusal's
text: the message names the shape refused and the form that works, so the
agent meets the remedy at the refusal and needs no memory. Find each
refusal by searching the payload for the text the memory quotes.

- [ ] **Step 1: Tests.**
  - For each `G` row, a case in the guard's or helper's existing suite
    asserting the remedy's words in the refusal — not the exit code alone
    (a deleted check with a green exit-code test is backend's memory
    *Exit codes make vacuous tests*).
  - `test_python_hygiene.py`: `test_no_text_true_without_encoding` and
    `test_no_bare_bash_argv` walk every `*.py` under `.claude/` and `.github/`
    of the root they run in; `test_hygiene_catches_a_planted_file` proves
    both on a synthetic file.
- [ ] **Step 2:** Run, fail; change the messages and fix every site the
  hygiene test names; run, pass. Run `payload-suites` as CI does.
- [ ] **Step 3:** Write `pitfalls.md` from the `P` rows, one short entry
  each: the trap, the symptom, the safe form.
- [ ] **Step 4:** Commit, PR, `/ship` loop, merge on green.

### Task 4: Citations a target can resolve

**Files:**
- Modify: the payload sites `IMPORT.md` *References to backend-local
  commands and documents* lists, `payload/docs/harness-boundaries.md`
  (*Managed files* gains the `docs/harness-local.md` contract; the lines
  that section lists as backend-only are cut), `IMPORT.md` (that section
  says what Task 4 did)
- Create: `harness_sync/tests/test_payload_references.py`,
  `docs/harness-local.md` in the harness repository root (its own: no
  change contract, no product spec, *Ship checks* none)

**Interfaces:**
- Consumes: Task 1's `imports`; Tasks 1–3's headings.

Rewrites, by kind:

| Payload cites | Becomes |
|---|---|
| A section of backend's `CLAUDE.md` | The `agent-rules.md` or `docs/harness/` section holding it now |
| `docs/change-locality.md` §6 | `docs/harness/working-rules.md` section |
| `docs/change-locality.md` §5, classes, touch set | "the change contract `docs/harness-local.md` *Documents* names" |
| `docs/token-plan.md` | `docs/harness/token-rules.md` section |
| `/validate-blueprint`, `/check-links`, `/review-branch`, `docs/roadmap.md`, `docs/backend-architecture/` | "the checks `docs/harness-local.md` *Ship checks* names" |
| `.claude/commands/review-branch.md` *What counts as a finding* | working-rules *What counts as a finding* |
| `docs/style-guide.md`, `docs/testing.md` | "the style guide / testing document `docs/harness-local.md` *Documents* names" |
| `docs/superpowers/` | unchanged — the superpowers plugin's default in every repository *(builder's call)* |

The boundaries lines cut here land in backend's `docs/harness-local.md`
*Residuals* in Task 6, before this task's sync merges there.

- [ ] **Step 1: Tests.**
  - `test_payload_cites_only_what_a_target_has` — every repository path a
    payload text file cites resolves under `payload/`, or is
    `docs/harness-local.md`, or sits under `docs/superpowers/`.
  - `test_cited_section_exists` — every `` `path` ``, *Heading* pair whose
    path is in the payload names a heading that file has (Review Focus 3).
  - `test_local_command_not_named` — no payload file names `/<name>` for a
    `.claude/commands/<name>.md` in any `targets/*.config.json` `local`.
  - `test_claude_md_section_not_cited` — no payload file cites `CLAUDE.md`
    followed by an italic section name.
  - One synthetic case each proving the four catch a planted citation.
- [ ] **Step 2:** Run, fail (the real payload fails the first and third);
  rewrite; run, pass. The stack mentions `IMPORT.md` lists stay, by Scope.
- [ ] **Step 3:** Commit, PR naming `Refs #7` (not a closing keyword: the
  stack half stays for plan 5), `/ship` loop, merge on green.

### Task 5: The harness takes its own rules

**Files:**
- Create: `CLAUDE.md` in the harness repository root (local): what the
  repository is, where the payload and the tool live, and
  `@.harness/agent-rules.md` on a line of its own

- [ ] **Step 1:** Merge the self-sync pull request that Task 4's merge
  opened (the earlier ones were superseded); its drift gate is green.
- [ ] **Step 2:** On a branch cut from that `main`, write `CLAUDE.md`;
  commit, PR, merge on green.
- [ ] **Step 3:** Relaunch a harness session; `/context` lists
  `.harness/agent-rules.md` among memory files, and no approval dialog was
  shown.

### Task 6: Backend's local half, before its sync merges

**Files (backend, Class D):**
- Create: `docs/harness-local.md` — *Documents*: `docs/change-locality.md`,
  `docs/style-guide.md`, `docs/testing.md`, `docs/backend-architecture/`,
  `docs/roadmap.md`; *Ship checks*: the `/validate-blueprint` and
  `/check-links` rules from `/ship` step 2 as backend has them, dispatched
  audit included; *Residuals*: the boundaries lines Task 4 cut.

The window: until Task 7 merges, the managed copies still say this too, and
the PR body names the managed copy as the owner until then.

- [ ] **Step 1:** Write it; check each *Residuals* line against the
  boundaries text it came from.
- [ ] **Step 2:** `/ship`; merge on green.

### Task 7: Backend's sync of the new rules

- [ ] **Step 1:** The sync pull request Task 4's merge opened (or a later
  one) is the one to land; earlier ones were superseded. Its diff: the four
  new managed files, the drift gate, the rewritten citations, the trimmed
  boundaries, the manifest. `CLAUDE.md` and `docs/harness-local.md` are not
  in it.
- [ ] **Step 2:** Before merging, run backend's local suites the sync
  touches (`test_command_runbooks` ceilings among them, harness #5); a
  failure is fixed in the payload and re-synced, never here.
- [ ] **Step 3:** One `branch-reviewer` round on the target-specific part
  (the manifest, and whether every `docs/harness-local.md` citation names a
  heading Task 6 wrote); merge on green and a clean round.

### Task 8: Backend's contract

**Files (backend, Class D):**
- Modify: `CLAUDE.md` — add `@.harness/agent-rules.md`; cut what Task 1
  moved, keeping what Task 1 lists as staying; its owner table gains the
  managed documents. `docs/change-locality.md` §6 — the moved bullets
  become one line citing `docs/harness/working-rules.md`.
  `docs/token-plan.md` — where it states a rule, it cites token-rules; its
  measurements stay. `.claude/commands/review-branch.md` — *What counts as
  a finding* cites working-rules.

- [ ] **Step 1:** Measure before: LF bytes of `CLAUDE.md` plus its imports,
  and of backend's `MEMORY.md` index.
- [ ] **Step 2:** Edit; grep that each cut rule's lead phrase now appears
  once across `CLAUDE.md`, `docs/`, `.claude/` and `.harness/`.
- [ ] **Step 3:** `/ship`; merge on green.
- [ ] **Step 4:** Relaunch; `/context` shows the import; no approval dialog.

### Task 9: Memory triage applied

- [ ] **Step 1:** For every backend row of Table M whose class is `A`, `W`,
  `T`, `P`, `G`, `C` or `S`, confirm its destination text is on backend's
  `main` (Review Focus 5); a row whose text is missing stops the move of
  that file only.
- [ ] **Step 2:** Move those memory files to
  `~/.claude/projects/C--dev-ashamray-blueprint-backend/memory-archive/2026-11/`
  and drop their lines from `MEMORY.md`. `R` rows stay. A split row
  (`gates-read-beyond-the-tree`) keeps only its backend half.
- [ ] **Step 3:** The `O` rows: write their rules into
  `~/.claude/CLAUDE.md` as one *Owner preferences* section, and move its
  citation of *The guards' threat model* from
  `alexander-shamray/blueprint-backend`'s `docs/harness-boundaries.md` to
  `alexander-shamray/blueprint-harness`'s; if that edit is refused, hand it
  over as a checked scratchpad script and a `!` line. Then archive the `O`
  memories of all three repositories.
- [ ] **Step 4:** Measure after, as Task 8 Step 1; record both figures and
  the next `/ship`'s stop count against `docs/baseline-2026-11.md` in the
  harness repository's `docs/baseline-2026-11.md`, *After plan 2*. A rise in
  stops is a harness defect, filed there.
- [ ] **Step 5:** Frontend's and admin's `A`–`S` memories wait for their
  first sync (plans 5, 6), which re-checks each row against that payload.

---

## Table M: the memory triage

Taken on 2026-11-03: 79 backend, 25 frontend, 19 admin. Classes: `A`
`agent-rules.md` *Handing over to the owner*; `W` working-rules; `T`
token-rules; `P` pitfalls; `G` a guard or helper's refusal (Task 3); `H` the
hygiene test; `C` a managed command runbook or skill; `O` owner preference,
to `~/.claude/CLAUDE.md` *(builder's call: these hold in every repository
but serve the owner's reading, not spec §2 goals 2–4)*; `S` spent; `R`
repository fact, stays. `S*` is spent from that repository's first sync.

| Class | Destination | backend | frontend | admin |
|---|---|---|---|---|
| A | *Handing over to the owner* | tell-user-when-denies-need-lifting, denied-edits-become-bang-scripts, hand-over-commands-with-bang | guard-edits-via-user-script, script-for-what-i-cannot-run, user-runs-blocked-edits | edit-denied-changes-via-patch-script |
| G | guard-git-argv refusal | worktree-guard-shell-shapes, issue-body-heredoc-shapes | guard-limits-found-on-b6 | — |
| G | commit-gate message | commit-types-are-closed-set | — | — |
| G | rebase helper refusal | never-merge-main-forward | local-denies-lift-blocks-rebase | — |
| H | `test_python_hygiene.py` | windows-subprocess-decoding, python-subprocess-bash-is-wsl | — | — |
| P | *Shell and encoding* | sed-i-drops-crlf, bash-heredoc-backslashes, python-print-crlf-in-bash, cacheinfo-mode-drops-exec-bit | — | shell-and-edit-traps |
| P | *Git and worktrees* | reset-soft-origin-main-moves, pipefail-before-chained-push, enterworktree-prompts-outside-claude-worktrees, worktree-session-leaves-empty-dir | subagents-cannot-ship-from-worktrees | — |
| P | *Machine and network* | localhost-costs-two-seconds, github-push-blocked-api-works, taskstop-leaves-grandchildren | — | — |
| P | *Auto mode and probes* | — | dependabot-pr-triage, adversarial-probes-need-a-dead-remote | boundary-probes-need-non-auto-mode |
| W | *CI* | wait-for-ci-to-settle, merge-as-soon-as-ready | always-check-and-fix-ci | check-ci-while-idle |
| W | *Review* | always-run-a-review-cycle, green-gates-say-nothing-about-prose, review-rounds-state-the-comment-bar | branch-reviewer-needs-criteria-file | — |
| W | *Tests and gates* | exit-codes-make-vacuous-tests, run-touched-suites-locally, stubbed-executables-need-posix-twin | mutate-the-class-not-the-sentence, verify-against-the-tree-not-your-list | — |
| W | *Commits* | parallel-implementers-commit-by-pathspec, comment-gate-owns-touched-blocks, gates-read-beyond-the-tree (comment-gate half) | — | — |
| W | *Issues and the task list* | harness-issue-label, todo-undated-rows-first, todo-item-notation | — | read-issue-comments-before-blocked-verdict |
| W | *Managed files* | harness-sync-pr-landing | — | — |
| C | `docs/commands/ship.md`, its Copilot and review steps | full-copilot-cycle-before-merge, copilot-suppressed-block-shape, copilot-request-wedges, copilot-review-feed-counts-owner, copilot-unavailable-land-on-subagent-rounds, never-head-limit-a-review-query, review-subagent-diff-location, ship-teardown-shared-checkout | requesting-copilot-pr-review, watching-for-copilot-review | copilot-wait-keyed-on-head |
| C | codebase-index `SKILL.md` | codebase-index-embeddings-rejected | — | — |
| T | *Briefing a subagent* | fewer-parallel-agents | — | — |
| O | `~/.claude/CLAUDE.md` | show-status-table-every-turn, keep-a-visible-task-list, end-status-with-apply-line, skip-copilot-review, skip-grok-review, cross-session-notes-via-file | end-status-with-apply-verdict, ship-denies-lifted-until-asked | status-ends-with-apply-verdict, copilot-reviews-disabled, denies-lifted-until-owner-asks |
| S | superseded by the user-level rule | file-unfixed-findings-at-pr-end, fix-code-findings-in-the-pr | file-unfixed-findings-at-pr-end | file-unfixed-findings-at-pr-end |
| S | `pr.md` *What the branch closes* says it | negated-closing-keyword-still-closes | — | partial-pr-must-not-close-issue |
| S | `ship.md` says it | — | — | decide-dont-ask-mid-chain |
| S | done or obsolete | hold-merges-until-dependabot-resolved, ship-finished-predicate-port, ci-wait-agent-180s-limit, transcript-error-results | harness-groups-run-order | copilot-loop-past-ceiling |
| S* | managed files change in the payload; `task_list` in config; one source | harness-edits-need-lift-and-restart, todo-md-unwritable-from-worktree | claude-edit-lock-needs-restart, todo-in-progress-before-worktree, check-sibling-repos-first | lift-needs-session-restart |
| R | stays | adr-slug-path-limit, domain-name-gate-chapter-prose, gates-read-beyond-the-tree (secret-scan half), hand-written-migration-keeps-ef-bom, host-rabbitmq-port-conflict, moved-text-agents-can-corrupt-samples, mutation-leaves-a-stale-binary, new-contract-needs-shapes-and-class-d-map, one-branch-per-service-plan, pr-locality-skips-class-tree-set, rabbitfixture-has-no-delay-plugin, realm-client-edit-trips-classifier, runbook-edits-are-pinned-by-game-day-tests, secret-scan-password-assignment, stacked-pr-gates, stress-run-after-rebuild, stress-run-commands-in-full, touch-set-row-paths-only, validate-blueprint-denies-src-edits-for-the-turn, waf-failed-start-disposal-race | code-docs-groups-run-order, npm-ci-must-be-clean, npm-test-is-ng-test, paren-branch-push-refused | fork-agents-work-under-ship-denies, harness-suite-runtime, lift-readme-deny-blocks-pull, merge-needs-automode-allow, new-npm-dependency-is-owner-run |

Where a `C` or `W` row's rule is already in its destination, the row is
`S` in effect: Task 9 Step 1 finds the text and archives the memory.
Lessons that a new guard could enforce but no guard does (`sed -i` on a
CRLF file, a chained push without `pipefail`) are written as `P` rules here
and each filed as a harness enhancement *(builder's call: new guards are
their own pull requests, with their own threat-model review)*.
