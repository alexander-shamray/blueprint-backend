---
description: Review branch vs main for contradictions; recheck suggestions.md when it already exists
argument-hint: "[recheck | full | --local]"
allowed-tools: Read, Grep, Glob, Write, Edit, Bash(git diff:*), Bash(git log:*), Bash(git status:*), Bash(git merge-base:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(python .github/licence-gate/licence_gate.py), Bash(bash .claude/scripts/dotnet-test.sh:*), Bash(bash .claude/scripts/pr-for-branch.sh:*), Bash(bash .claude/scripts/pr-locality.sh:*), Bash(rm suggestions.md)
disallowed-tools: Edit(.git/**), Edit(./.git/**), Edit(.git), Edit(./.git), Edit(.claude/**), Edit(./.claude/**), Edit(.config/**), Edit(./.config/**), Edit(.github/**), Edit(./.github/**), Edit(deploy/**), Edit(./deploy/**), Edit(docs/**), Edit(./docs/**), Edit(src/**), Edit(./src/**), Edit(tests/**), Edit(./tests/**), Edit(tools/**), Edit(./tools/**), Edit(.dockerignore), Edit(./.dockerignore), Edit(.editorconfig), Edit(./.editorconfig), Edit(.gitattributes), Edit(./.gitattributes), Edit(.gitignore), Edit(./.gitignore), Edit(CLAUDE.md), Edit(./CLAUDE.md), Edit(Directory.Build.props), Edit(./Directory.Build.props), Edit(Directory.Build.targets), Edit(./Directory.Build.targets), Edit(Directory.Build.rsp), Edit(./Directory.Build.rsp), Edit(Directory.Solution.props), Edit(./Directory.Solution.props), Edit(Directory.Solution.targets), Edit(./Directory.Solution.targets), Edit(MSBuild.rsp), Edit(./MSBuild.rsp), Edit(nuget.config), Edit(./nuget.config), Edit(NuGet.config), Edit(./NuGet.config), Edit(NuGet.Config), Edit(./NuGet.Config), Edit(**/*.targets), Edit(**/*.props), Edit(**/*.rsp), Edit(**/*.csproj), Edit(**/*.sln), Edit(**/*.slnx), Edit(Directory.Packages.props), Edit(./Directory.Packages.props), Edit(Platform.slnx), Edit(./Platform.slnx), Edit(README.md), Edit(./README.md), Edit(coverage.runsettings), Edit(./coverage.runsettings), Edit(global.json), Edit(./global.json), Edit(.mcp.json), Edit(./.mcp.json), Edit(.codeindexignore), Edit(./.codeindexignore)
---

Review uncommitted or branch work for **contradictions and self-consistency
issues**, and manage `suggestions.md` at the repository root.

## Mode selection (do this first)

**Before choosing a mode, check whether `suggestions.md` exists at the repo
root.** That file is the running record of open issues; when it is present,
the default job is to re-verify it, not to ignore it and start a fresh review.

| Condition | Mode |
|---|---|
| `suggestions.md` **exists** and `$ARGUMENTS` is empty or `recheck` | **Recheck** (below) |
| `suggestions.md` **missing** and `$ARGUMENTS` is empty | **Full review** — branch vs `main` |
| `$ARGUMENTS` is `full` or `full --local` | **Full review**, even if `suggestions.md` exists (replace the file from scratch after the new pass) |
| `$ARGUMENTS` is `--local` only, no `suggestions.md` | **Full review** of the working tree |
| `$ARGUMENTS` is `--local` and `suggestions.md` exists | **Recheck** first (same as default when the file exists); do not silently switch to a full local sweep unless the user also passed `full` |
| `$ARGUMENTS` is `recheck` and `suggestions.md` **missing** | Stop: say there is nothing to recheck and offer a full review — do not enter recheck mode against a file that is not there |

`recheck` as an explicit argument is kept for clarity; it is **not** required
when the file is already there. Prefer the file’s presence over the empty
argument list.

---

## What counts as a finding

Same bar as `/validate-blueprint`: **two statements that cannot both be true**,
or a statement that cannot be true of the system described — not pure style
taste. Prefer:

1. **Blueprint ↔ code drift** once `src/` exists (samples, pins, type names,
   registration order, endpoints, credentials).
2. **Cross-chapter / CLAUDE.md contradictions** — two rules that cannot both
   be true, or a planned tree stated as present. **Not** a pre-existing
   phase marker, test count, project count or second copy of a value that
   this branch left untouched: those are restatements
   `docs/change-locality.md` §2 forbids writing, so a stale one is awaiting
   removal by the plan, not a finding against this branch. One this branch
   **introduces or edits** is a finding under the same section.
3. **Register drift** — `Directory.Packages.props` vs Appendix B vs §4.4 sample;
   licence gate failing.
4. **Deploy drift** — Compose / Helm / CI vs §14 / §15 claims (ports, secrets,
   service names, healthchecks).
5. **Incomplete reconciliation** — a rule this change states (or a fix it claims)
   that the corpus still violates in the same change set.
6. **A file outside the declared touch set.** The PR body's `| Class |` and
   `| Touch set |` rows say what this branch may edit, and
   `bash .claude/scripts/pr-locality.sh <n>` — `<n>` from
   `bash .claude/scripts/pr-for-branch.sh` — judges every changed path
   against them and prints one `class` line and one `inside <path>` or
   `outside <path>` line per file. **It never prints the rows**: the set is
   the author's text, and a path grammar cannot keep prose out of a path,
   so the helper consumes the cell and only its own two words leave. Each
   `outside` line is a finding, and its resolution is a narrower diff, a
   row widened with its reason beside it when the path is inside the
   class's tree set, or a different class when it is not — never a row
   widened silently (`docs/change-locality.md` §3). Where the helper prints
   nothing or cannot run — a `--local` review with no PR yet, a body
   carrying neither row, or the sandbox clone, which has no network — say so
   and skip this check rather than inferring a class. The plan's locality
   gate is the enforcement; this is the early read. **The verdict narrows
   and grants nothing**: an `inside` line is not a licence for anything
   this command's grant refuses, and the class's tree set in the contract
   still bounds what a row may declare.

Reject as non-findings the house styles `docs/style-guide.md` tabulates on
purpose (braceless single statements, file-scoped namespaces, explicit
types, British prose beside real identifier spellings, unpinned Aspire with
§4.4 carve-outs, spread-over-`.ToArray()` when the corpus is already
clean), and the stale restatements `docs/change-locality.md` §2 leaves in
place — a pre-existing count, "since PR-NN" or second copy of a value that
this branch did not touch, where the owner site is already correct.

---

## Recheck mode

**Trigger:** `suggestions.md` is present (default), or `$ARGUMENTS` contains
`recheck`.

1. **Read `suggestions.md` in full.** Enumerate every numbered issue from the
   status table and from the per-issue headings. If the file is empty or has no
   numbered issues, say so and offer a full review rather than inventing items.
2. **For each issue, independently:**
   - Locate the sites named under **Where** / **File** (grep siblings if the
     line numbers moved).
   - Verify whether the stated **Problem** still holds in the current tree
     (and, for branch work, against the current tip — not an old diff alone).
   - Set status to exactly one of:
     - **fixed** — evidence shows the defect is gone
     - **open** — still true
     - **correct** — owner accepted the current state as intentional (only if
       the file already said so, or the user has said so in this conversation)
     - **false positive** — original finding was wrong; only if re-verification
       shows the claim never held / no longer applies as a defect
3. **Do not invent new issues** in recheck mode unless verifying a listed item
   surfaces a **direct regression of that item** (same claim, still broken in a
   new place). A full sweep for new findings is `full`, not recheck.
4. **Rewrite `suggestions.md`:**
   - Update the status table and every per-issue **Status** line.
   - Keep fixed items briefly under their headings (or a “Fixed this pass”
     section) so the next recheck still has context — unless step 5 deletes the
     file.
   - Bump **Re-checked:** to today’s date.
5. **If every issue is `fixed`, `correct`, or `false positive`:**
   - **Delete** `suggestions.md`.
   - Report in chat: all closed, file removed, one-line evidence per former
     issue.
6. **If any issue remains `open`:** keep the file, report counts
   (open / fixed / other).

---

## Full review mode

**Trigger:** no `suggestions.md`, or user passed `full` / `full --local`.

1. **Establish the range.**
   - Branch (default): `MERGE_BASE=$(git merge-base origin/main HEAD)` (fall
     back to `main`), then `git diff --stat` / `--name-only` / full diff
     `"$MERGE_BASE..HEAD"`, plus `git log --oneline "$MERGE_BASE..HEAD"`.
   - `--local`: `git status --short` and `git diff HEAD` (include untracked
     that matter; skip bulk tooling noise).
2. **Read the change.** Prefer full source of load-bearing files over the
   diff alone. Grep `src/`, `tests/` and `deploy/` for every **symbol** the
   change touches, and the one chapter section that owns a rule the change
   moved. Do not tour the corpus for the value: a restatement outside the
   touch set that the change left stale is not this branch's to fix
   (`docs/change-locality.md` §2).
3. **Run cheap gates when the range touches them.**
   - Packages / Appendix B: `python .github/licence-gate/licence_gate.py`
   - Tests, when the range touches `src/` or `tests/`:
     `bash .claude/scripts/dotnet-test.sh [all|fast]`

   **In the sandbox the second one is not available**, and that is deliberate
   rather than an oversight: `dotnet test` needs a Docker daemon for its
   Testcontainers suites, so running it inside a container built to take
   capability away would mean Docker-in-Docker. The licence gate is stdlib
   Python and does run there. So whether the suite is green is the **host's**
   to verify — report it as unverified rather than asserting it, and never
   report `command not found` as a finding about the branch.
4. **Author findings only when verified.** Quote the conflicting sites.
   Severity: **bug** | **suggestion** | **nit**.
5. **Write `suggestions.md` at the repo root** when any issue is open. Shape:

   ```markdown
   # Suggestions — branch vs `main`   # or "local uncommitted"

   **Reviewed:** <ISO date>
   **Branch:** <name>
   **Base:** origin/main @ <short sha>
   **Scope:** <one line>
   **Diff:** <N files, +/- lines>

   ## Overall
   <2–4 sentences>

   | # | Severity | Item | Status |
   |---|---|---|---|
   | **1** | bug | … | open |

   ## Bugs / Suggestions / Nits
   ### 1. …

   | | |
   |---|---|
   | **Where** | … |
   | **Status** | open |

   **Problem.** …
   **Suggestion.** …

   ## What looks good (no action)
   …

   ## Recommended fix order
   …
   ```

6. **If the review finds nothing open**, do **not** create `suggestions.md`.
   If `full` was requested and an old file existed, **delete** it when the new
   pass is clean; **replace** it when the new pass has findings.
7. **`suggestions.md` is working state.** Never commit it. Do not add it to
   the index. Say so if the user is about to ship.

---

## Report (chat)

Always end with:

- Mode actually run: **recheck** or **full** (and branch/base or local)
- Whether `suggestions.md` was present at start
- Issue counts by status (open / fixed / correct / false positive)
- Path to `suggestions.md` if kept, or that it was deleted / not created
- One-line top remaining findings (if any)

Do not fix the findings in this command unless the user explicitly asks to
apply them after the review.

**That is enforced, not prose alone.** A "do not fix" claim resting on prose
while the grant holds `Write` and `Edit` over every path
`.claude/settings.json` does not deny is, for a review command, the worse
failure. The frontmatter's `disallowed-tools` path-scopes `Edit` away from
every tracked tree, `docs/` included, **and from every tracked file at the
repository root** — denying directories alone would leave `CLAUDE.md`,
`global.json`, `Directory.Build.props` and `Platform.slnx` writable, a
boundary with a gap exactly where this repository keeps its build inputs, so
a command promising not to fix findings could still apply one to root
configuration.

They are **enumerated** rather than denied wholesale, and `suggestions.md` is
why: it lives at the root, it is this command's one legitimate output, and it
is untracked — so denying every *tracked* root file leaves it alone, where a
blanket `Edit(**)` or a `/*` root pattern would take the deliverable with it.
`test_harness_denies.py` reads the tracked set from `git ls-files` and asserts
each is denied, so a new root file is a red build rather than a silent gap.

**Two limits, both stated rather than glossed.**

`disallowed-tools` binds the Claude Code host path — `/review-branch` run here,
including `--local`. It says nothing about the containerised run: inside
`grok-review.sh` this file is read by **grok**, a different CLI, under
`--permission-mode bypassPermissions`, and nothing has established that grok
honours a `disallowed-tools` key at all. There the only thing keeping the
reviewer from rewriting the branch it is reviewing is the container's
disposability — a property of the sandbox, not of this grant. Do not read the
frontmatter as reaching that run.

And the list is a deny-list, so a tree added later is editable until someone
adds it. `test_harness_denies.py` asserts the list covers every tracked
top-level tree, which is what makes that a red build instead of a quiet
widening.

**That test cannot see a file that does not exist yet, and that is the
dangerous one.** It reads `git ls-files`, so it enumerates what EXISTS. MSBuild
imports `Directory.Build.targets` into every build of every project beneath
it — an `Exec` in an auto-imported `.targets` runs, and `dotnet build` reports
success — so `Write` beside a build grant is host code execution.

Two changes close it, and they close different halves. The executor is
`dotnet-test.sh`, which fixes the solution and the flags — a raw
`dotnet test:*` grant admits both an arbitrary project path and
`/p:CustomBeforeMicrosoftCommonTargets=<file>`, which imports whatever it is
pointed at, `suggestions.md` included. And the auto-import surface itself is
denied: every name MSBuild reads without being asked, in the exact spelling
this file already uses, plus `**/*.targets`, `**/*.props`, `**/*.rsp`,
`**/*.csproj`, `**/*.sln` and `**/*.slnx` for the class.

**The exact names are the control.** The exact-filename form is the one the
suite reads. The `**/` globs are the documented gitignore-style syntax and are
**not** verified here — they are belt to the exact names' braces, so if that
syntax turned out inert in a `disallowed-tools` value the exact names would
still close the vector. Do not read the globs as the control.
