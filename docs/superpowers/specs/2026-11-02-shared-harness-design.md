# Shared harness — design

One repository, `blueprint-harness`, owns the agent harness that
`blueprint-backend`, `blueprint-frontend` and `blueprint-admin` each carry
today, and delivers it to them by pull request.

Calls marked *(builder's call)* were made while writing this down, not agreed
with the owner; everything else was agreed in the session of 2026-11-02.

## 1. Why

The same rule is written by hand three times. Measured on 2026-11-02 over the
tracked `.claude/`, `docs/commands/`, harness docs and harness gates of the
three checkouts:

| | Files | Lines (largest copy) |
|---|---|---|
| Present in two or three repositories | 92 | ~47 000 |
| — byte-identical | 3 | 289 |
| — at least 80% of lines shared | 17 | ~3 400 |
| — diverged below 80% | 72 | ~43 800 |
| Present only in backend | 66 | ~20 800 |
| Present only in frontend / only in admin | 14 / 18 | ~4 700 / ~4 600 |

The shared files implement the same mechanisms and have drifted through
porting: a fix lands in one copy and not the others (backend's `pr-state.sh`
reads through `gh-read-bound.sh`, frontend's does not), and comments are
rewritten on each port. In the 30 days before the measurement the three
repositories took about 350, 230 and 335 harness commits, of which 83, 98 and
145 mention a port or a sibling repository: roughly a third of harness work
is moving work between copies.

The shared files carry almost nothing stack-specific: `guard-*.py`,
`ship.md` and `git-*.sh` name neither `dotnet` nor `Platform.slnx` in
backend; the stack lives in a handful of files (`dotnet-test.sh`,
`npm-checks.sh`, admin's `pin-gate`, CI workflows, the allow list).

## 2. Goals

1. **One source.** A harness rule is written and changed in one place and
   reaches all three repositories from there.
2. **Autonomy.** Agents do as much as possible without stopping for the
   owner.
3. **Protection from the most destructive actions** on the repositories and
   on the developer's machine. Concretely, `main` cannot be damaged: no direct
   push, no rewrite of its history, no deletion, merges only by pull request.
   Force-push to any other branch is allowed (a change from backend today,
   which denies `git push --force*` and `git branch --force` outright).
   Protection is against an agent's mistake and a prompt-injected
   instruction, not against a spelling built to evade a guard — the scope
   `docs/harness-boundaries.md`, *The guards' threat model*, already sets.
4. **Token economy.** The measures in `docs/token-plan.md` apply to all three
   repositories, not only backend.
5. **Lessons become rules.** What the per-project Claude Code memories hold
   that serves goals 2–4 becomes a rule of the shared harness; the memory
   copies are then removed.

## 3. Non-goals

- Unifying what describes the product rather than the agent's work: the
  blueprint, `docs/style-guide.md`, `docs/testing.md`, `docs/repo-map.md`,
  backend's domain gates (`messaging-gate`, `domain-name-gate`,
  `sql-style-gate`, `pipeline-gate`, `licence-gate`, `secret-scan`), admin's
  `pin-gate`, the stack's build and test commands.
- Delivery outside a pull request: no Claude Code plugin, no submodule, no
  update that lands in a repository at session start.
- Guarding against deliberate guard evasion (goal 3's scope).
- Making the harness usable by anyone but these three repositories
  *(builder's call: nothing is generalised for a hypothetical fourth)*.

## 4. Anti-goals

The move has failed, even if every part works, when either holds:

- **`main` breaks** in any of the three repositories — a red required check
  on `main`, or a harness change that lets an agent damage `main`.
- **Agents stop more often.** A stop is a permission prompt, an
  `AskUserQuestion`, or a handover the owner must run (`⚠️ ACTION REQUIRED`,
  a `!` script). The count per typical task must not rise in any repository;
  a rise is a defect of the sync, not a cost of the move.

Both are measured (§9), not judged.

## 5. Decided shape

### 5.1 The harness repository

`alexander-shamray/blueprint-harness`, checked out at
`C:\dev\ashamray\blueprint-harness` *(name and path: builder's call)*.

- **Source.** The canonical files live under a payload directory laid out as
  a target repository is (`payload/.claude/hooks/…`,
  `payload/.claude/scripts/…`, `payload/docs/commands/…`,
  `payload/.github/…`). Payload files are not live in a harness-repository
  session, so agents edit them without the `.claude/` self-protection denies
  and without `!` apply scripts — the largest single cut in stops this design
  makes.
- **Self-hosting.** The harness repository is its own fourth sync target: its
  root `.claude/` is rendered from `payload/` like any other, through a sync
  pull request like any other — never a commit to its `main` — so its own
  sessions run under the harness they develop, behind one released version.
- **Its own `main`** is the one review gate before a hook or deny change
  reaches all three targets. It gets the same no-bypass ruleset as the
  targets (§5.5) and every change to it runs the full `/ship` loop. A change
  to the protection surface — denies, guard hooks, rulesets — merges on that
  loop like any other, with no approval by the owner: the owner decided so,
  for autonomy, accepting that review and the guards' own tests are what
  stand between a weakened guard and all three targets.
- **Tests.** The harness test suites (backend's `.claude/scripts/test_*.py`,
  the gates' tests, 691 tests in CI's `review-helpers` today) move with the
  code and run in the harness repository's CI against the payload.
- **Base.** Backend's implementation is the canonical starting point, as the
  most worked-through. A sibling's version is not merged in; a mechanism only
  a sibling has is taken over only where backend lacks it (§8).

### 5.2 Common file plus local setting

Every managed file is common; a repository differs only through its own
setting, never by replacing a managed file.

- **`.harness/config.*`** in each target *(format: builder's call)* holds the
  repository's facts: its slug, its stack's check commands (build, test,
  format), its gate list, its CI job names, its task-list path, its extra
  allow rules and MCP servers, and the list of paths it keeps local.
  Scripts and hooks read their per-repository values from it at run time
  rather than carrying them, so they stay byte-identical across targets.
- **`.claude/settings.json`** is rendered at sync time: the harness's base
  (denies, guard hooks, the common allow list) merged with the config's
  additions. Additions may add allows, denies, hooks and MCP servers —
  frontend's `package.json` and `tsconfig*.json` denies and admin's
  `Directory.Packages.props` and `*.slnx` denies stay local this way — but
  never remove or narrow a base deny or hook. A sibling deny that contradicts
  a base mechanism (frontend's `Bash(gh pr merge:*)` against backend's
  `gh-pr-merge.sh`) is dropped in favour of the base *(builder's call)*. The
  rendered file is committed as today and Claude Code reads it as today.
- **`CLAUDE.md`** splits. The agent's working rules go to a managed file the
  target's own `CLAUDE.md` pulls in with Claude Code's `@path` import; the
  target's `CLAUDE.md` keeps only what describes its product. The managed
  part is written once and short, rather than three times and growing.
- **Docs.** `docs/harness-boundaries.md` splits into the harness's common
  boundaries (managed) and the target's residuals (local). The working rules
  of `docs/change-locality.md` §6, and the measures of `docs/token-plan.md`
  as rules, move to the harness; each repository keeps its own measurements.
- Where a markdown command must name a stack command, it names the config
  key, not the command *(builder's call: render-time substitution only where
  that cannot work, and a rendered file is hashed after rendering)*.

### 5.3 Delivery

- A merge to the harness repository's `main` produces one sync pull request
  in each target *(trigger: builder's call — automatic on merge, from the
  harness repository's CI)*.
- The sync pull request rewrites the managed paths to the released payload,
  re-renders `settings.json`, and updates **`.harness/manifest.json`**: the
  harness commit it was taken from and the hash of every managed file.
- The sync is written by the harness repository's CI, not by a session in the
  target. A target session therefore never needs to write its own `.claude/`
  to take a sync, and the self-protection denies there stay as they are.
- **`/dispatch` takes sync pull requests up** as a group of their own in each
  target: it runs the review round and merges, so a sync needs no launch by
  the owner. No Claude runs inside CI.
- **Merge bar: required checks green and one clean review round** by a
  read-only `branch-reviewer`. The reviewer is handed the target-specific
  part of the diff — config, rendered `settings.json`, and any local file the
  change touches — not the managed files, which were reviewed once in the
  harness repository *(builder's call)*. The full `/ship` loop of several
  rounds stays for changes to the harness repository itself.
- The sync pull request's title, body and commits pass each target's own
  required gates (`commit-gate`, `closure-gate`, `locality-gate` in backend);
  the locality class map knows the managed paths.

### 5.4 The drift gate

A check in every target's CI fails when a managed file differs from its hash
in the manifest, when a managed path is missing, or when a path is neither
managed nor listed as local but sits under a managed directory. A local edit
to a managed file is therefore a red check, not a silent fork; the change is
made in the harness repository or the path is declared local in the config.
Like every gate here, it ships with a test whose subject is the surface it
watches (CLAUDE.md, *A gate that silently stops covering the newest
surface*).

### 5.5 Protecting `main` and the machine

- **Server side**, the backstop: each repository's `main` is protected by a
  GitHub ruleset — no force-push, no deletion, pull request required, the
  repository's required checks. The harness owns the ruleset as data and a
  check that reads it back *(builder's call: applied by a helper, verified in
  CI, since a local guard alone fails open on a missed spelling)*.
- **Client side**, the guards: backend's `guard-git-argv.py`,
  `guard-edit-target.py`, `guard-index-argv.py` and its deny list are the
  base. The branch policy narrows from "no force anywhere" to "no force,
  delete, reset or direct push that reaches `main`"; force-push to other
  branches goes through the existing push helpers, `--force-with-lease`
  form *(builder's call)*.
- **Order.** In each repository the ruleset is confirmed by reading it back
  before that repository's client deny on force is relaxed. Backend's
  ruleset already forbids deletion and non-fast-forward on `main`; frontend's
  and admin's are checked, and created where missing, first *(builder's
  call)*. Until then `main` is never guarded by the client alone.
- The destructive classes the guards cover — on repositories and on the
  machine — are listed once, in the harness's boundaries document, each with
  a test that the guard refuses it.

### 5.6 Token economy

`docs/token-plan.md`'s measures become rules of the shared harness: the
always-loaded instruction set stays short, with detail loaded on demand;
subagents are briefed with what they need and return `file:line`; reviewers
get the diff, not the tree; a subagent's model follows who catches its error
(step 7d); a wait is one background task that wakes once (step 7). In
addition, the managed always-loaded part (the imported `CLAUDE.md` plus
anything else every session reads) is no larger than backend's current
`CLAUDE.md` *(builder's call on the limit; a gate holds it)*.

### 5.7 Memory triage

The per-project memories — 79 in backend, 26 in frontend, 20 in admin, plus
any under worktree project directories — are each classified once:

- **Harness rule**: serves goals 2–4 and holds in every repository (for
  example *sed -i drops CRLF*, *Windows subprocess decoding*, *Pipefail
  before chained push*). It is written into the harness — as a guard or
  helper where it can be enforced, as a rule where it cannot — and the
  memory removed.
- **Repository fact**: true of one repository only; it stays in that memory.
- **Spent**: superseded or obsolete; removed.

The classification is a table in the plan, and memories are removed only
after their rule has landed.

## 6. Constraints

- Claude Code finds commands, agents, skills, hooks and settings only in
  fixed places under `.claude/`; managed files land at those paths.
- File permission rules take `Edit(...)`, never `Write(...)`; a `Write(path)`
  rule stops Claude Code from starting. `.claude/settings.json` self-locks:
  a change to it lands complete and goes last, and a restore is verified by
  reading the file (`docs/harness-boundaries.md`, *Settings self-lock and
  permission root*). Rendering must respect both.
- Permissions cache at session start: a session sees a sync only after a
  relaunch.
- Windows checkouts: hashes are taken over LF-normalised content, and the
  sync never rewrites line endings (a CRLF `.cs` file turned LF fails
  IDE0055 in backend).
- `py -3.12`, not `python`; Git's `bash.exe` by absolute path from Python.
- Backend's one rule — every fact has one owner — moves with the harness:
  once a rule's owner is the harness repository, a target cites it there and
  never restates it. The user-level `~/.claude/CLAUDE.md` cites backend's
  *The guards' threat model*; that citation moves to the harness.
- Each repository's `main` stays green at every step of the rollout.

## 7. Rollout

Each step ends with every target's required checks green on `main`.

0. **Baseline.** Count stops per typical task (a `/ship` of a small pull
   request, a sweep round) in each repository from recent transcripts, by the
   §4 definition. This is the figure §9 compares against.
1. **Harness repository.** Created with backend's harness as its payload, its
   tests, its CI and its self-sync; the common docs and rules (§5.2, §5.6)
   written there.
2. **Backend first.** Its sync diff should be close to empty apart from the
   manifest, the config, the `CLAUDE.md` split and the docs move — a test of
   the mechanism on the repository it was taken from.
3. **Frontend, then admin.** Each sibling-only mechanism is decided (§8)
   before its repository's first sync, because a sync replaces what it does
   not know about. Their local differences become config or are dropped where
   backend already solves the same problem. Before a sibling's first sync,
   each of its diverged shared files is audited against backend's for fixes
   that exist only in the sibling's copy; a fix backend lacks goes into the
   payload, with its test, first *(builder's call: one audit per file
   cluster, by read-only subagents, returning `file:line` and the fix)*.
   This is expected to be the largest work item of the rollout.

Harness work during the rollout *(builder's call)*: from step 1, harness
changes are made in the payload, not in a target. A target's own `.claude/`
is frozen from the moment its first sync is opened until it merges. Harness
branches and pull requests already open in a target land before that
target's first sync, or are re-made against the payload after it.
4. **Drift gate on** in all three, and the porting rules and memories it
   makes obsolete removed.

## 8. Sibling-only mechanisms to decide

Taken from the 2026-11-02 measurement; each is taken into the payload,
kept local, or dropped, and the plan records which.

| Repository | Mechanism | Likely home |
|---|---|---|
| frontend | `index-refresh.py` hook and `test_index_refresh.py` | compare with backend's `refresh-index.py` |
| frontend | `git-fetch-origin.sh`, `git-pull-ff-only.sh` | payload if backend lacks an equivalent |
| frontend | `run-harness.py`, `in_process_hook.py` | compare with backend's suite runner |
| frontend | codebase-index `gate-index.py`, `gate.sh`, `mcp-launch.mjs`, `run-index` | compare with backend's codebase-index skill |
| admin | `index-hint-run.sh`, `index-query-hint.sh`, `refresh-index.sh` | compare with backend's Python hooks |
| admin | `shard-harness-suite.py` | payload if CI time needs it |
| admin | `main-red-open.sh` | payload (guards `main`) |
| admin | `harness-checks.sh`, `host-checks.sh`, `spa-ci.sh` | config or local |
| admin | `pin-gate`, `admin-invariants` skill | local |

The 66 backend-only files (`/dispatch`, `/team`, quality sets, sweeps,
mutation, review helpers) are payload, except what depends on the .NET stack
or the blueprint (`dotnet-test.sh`, `mutation-run.sh` and the Stryker files,
`/validate-blueprint`, `/new-adr`, `/new-chapter`, `/check-links`), which
stays local to backend *(builder's call per file)*.

## 9. Success

- Every rule in the shared set has one copy, in the harness repository; the
  drift gate is green in all three targets.
- A harness change reaches all three targets through one harness pull
  request and three sync pull requests, each merged on green checks and one
  review round.
- Stops per typical task (§7 step 0) have not risen in any repository.
- No red required check on any `main` during or after the rollout.

## 10. Left to the builder

The config format; the sync tool's language (Python 3.12, as the rest of the
harness, is the default); how the harness repository's CI authenticates to
open pull requests in the targets; the exact managed/local split per file
within §5 and §8; the ruleset helper; how stops are counted from transcripts;
the plan's step order within §7.
