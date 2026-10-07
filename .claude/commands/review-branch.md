---
description: Review branch vs main for contradictions; recheck suggestions.md when it already exists
argument-hint: "[recheck | full | --local]"
allowed-tools: Read, Grep, Glob, Write, Edit, Bash(git diff:*), Bash(git log:*), Bash(git status:*), Bash(git merge-base:*), Bash(git branch --list:*), Bash(git branch --show-current), Bash(git branch -a), Bash(python .github/licence-gate/licence_gate.py), Bash(bash .claude/scripts/dotnet-test.sh:*), Bash(bash .claude/scripts/pr-for-branch.sh:*), Bash(bash .claude/scripts/pr-locality.sh:*), Bash(rm suggestions.md)
disallowed-tools: Edit(.git/**), Edit(./.git/**), Edit(.git), Edit(./.git), Edit(.claude/**), Edit(./.claude/**), Edit(.config/**), Edit(./.config/**), Edit(.github/**), Edit(./.github/**), Edit(deploy/**), Edit(./deploy/**), Edit(docs/**), Edit(./docs/**), Edit(src/**), Edit(./src/**), Edit(tests/**), Edit(./tests/**), Edit(tools/**), Edit(./tools/**), Edit(.dockerignore), Edit(./.dockerignore), Edit(.editorconfig), Edit(./.editorconfig), Edit(.gitattributes), Edit(./.gitattributes), Edit(.gitignore), Edit(./.gitignore), Edit(CLAUDE.md), Edit(./CLAUDE.md), Edit(Directory.Build.props), Edit(./Directory.Build.props), Edit(Directory.Build.targets), Edit(./Directory.Build.targets), Edit(Directory.Build.rsp), Edit(./Directory.Build.rsp), Edit(Directory.Solution.props), Edit(./Directory.Solution.props), Edit(Directory.Solution.targets), Edit(./Directory.Solution.targets), Edit(MSBuild.rsp), Edit(./MSBuild.rsp), Edit(nuget.config), Edit(./nuget.config), Edit(NuGet.config), Edit(./NuGet.config), Edit(NuGet.Config), Edit(./NuGet.Config), Edit(**/*.targets), Edit(**/*.props), Edit(**/*.rsp), Edit(**/*.csproj), Edit(**/*.sln), Edit(**/*.slnx), Edit(Directory.Packages.props), Edit(./Directory.Packages.props), Edit(Platform.slnx), Edit(./Platform.slnx), Edit(README.md), Edit(./README.md), Edit(coverage.runsettings), Edit(./coverage.runsettings), Edit(global.json), Edit(./global.json), Edit(.mcp.json), Edit(./.mcp.json), Edit(.codeindexignore), Edit(./.codeindexignore)
---

Review branch or local work for **contradictions**, and manage
`suggestions.md` at the repo root.

## Mode selection (do this first)

Check whether `suggestions.md` exists first; its presence beats an empty
`$ARGUMENTS`.

| Condition | Mode |
|---|---|
| `suggestions.md` **exists** and `$ARGUMENTS` is empty or `recheck` | **Recheck** (below) |
| `suggestions.md` **missing** and `$ARGUMENTS` is empty | **Full review** — branch vs `main` |
| `$ARGUMENTS` is `full` or `full --local` | **Full review**, even if `suggestions.md` exists (replace the file from scratch after the new pass) |
| `$ARGUMENTS` is `--local` only, no `suggestions.md` | **Full review** of the working tree |
| `$ARGUMENTS` is `--local` and `suggestions.md` exists | **Recheck** first (same as default when the file exists); do not silently switch to a full local sweep unless the user also passed `full` |
| `$ARGUMENTS` is `recheck` and `suggestions.md` **missing** | Stop: say there is nothing to recheck and offer a full review — do not enter recheck mode against a file that is not there |

## What counts as a finding

**Two statements that cannot both be true**, or one untrue of the system;
never style taste. Prefer:

1. **Blueprint ↔ code drift**: samples, pins, type names, registration order,
   endpoints, credentials.
2. **Cross-chapter / CLAUDE.md contradictions**, or a planned tree stated as
   present; a stale restatement is one only if this branch **introduces or
   edits** it (`docs/change-locality.md` §2).
3. **Register drift**: `Directory.Packages.props` vs Appendix B vs
   `allowed-licences.txt`, a chapter pin as an `Include`/`Version` pair, or
   the licence gate failing.
4. **Deploy drift**: Compose / Helm / CI vs §14 / §15 (ports, secrets,
   service names, healthchecks).
5. **Incomplete reconciliation**: a rule or fix this change states that the
   corpus still violates in the same change set.
6. **A file outside the declared touch set**: each `outside <path>` line of
   `bash .claude/scripts/pr-locality.sh <n>` (`<n>` from
   `bash .claude/scripts/pr-for-branch.sh`), resolved per
   `docs/change-locality.md` §3, never by a row widened silently; if it
   prints nothing or cannot run, say so and skip, never infer a class; an
   `inside` line grants nothing (why: docs/commands/review-branch.md, *What
   counts as a finding*).

Never raise `docs/style-guide.md`'s tabulated house styles, a stale
restatement this branch left untouched where the owner site is already
correct, or a request that a true comment keeping its *Comments* rules be
reworded, completed or expanded. A diff
wholly under `docs/superpowers/` gets only what `.claude/commands/ship.md`'s
*A plan is reviewed once, for contradiction* admits.

## Recheck mode

1. **Read `suggestions.md` in full**; enumerate every numbered issue in its
   table and headings. If none, say so and offer a full review.
2. **Re-verify each issue** at its **Where** / **File** sites (grep siblings
   if the line numbers moved) in the current tree (and on the current tip,
   for branch work); set exactly one of **fixed**, **open**, **correct**
   (only if the file or the user already said so), **false positive** (never
   held or no longer applies as a defect, including a *Comments*-rule
   rewording request).
3. **Do not invent new issues** beyond a direct regression of a listed item.
4. **Rewrite `suggestions.md`**: table, every **Status** line, fixed items
   kept briefly, **Re-checked:** today.
5. **If every issue is `fixed`, `correct`, or `false positive`:** delete
   `suggestions.md`; report one line of evidence per former issue.
6. **If any issue remains `open`:** keep the file, report counts.

## Full review mode

1. **Establish the range.** `MERGE_BASE=$(git merge-base origin/main HEAD)`
   (else `main`); `git diff --stat` / `--name-only` / full, and
   `git log --oneline`, over `"$MERGE_BASE..HEAD"`. `--local`:
   `git status --short`, `git diff HEAD`, untracked files that matter.
2. **Read the change**, load-bearing files whole; grep `src/`, `tests/`,
   `deploy/` for each touched symbol and the section owning a moved rule;
   never tour the corpus for a value (`docs/change-locality.md` §2).
3. **Run gates the range touches**: packages / Appendix B,
   `python .github/licence-gate/licence_gate.py`; `src/` or `tests/`,
   `bash .claude/scripts/dotnet-test.sh [all|fast]`.
4. **Author findings only when verified**, quoting sites. Severity: **bug** |
   **suggestion** | **nit**.
5. **Write `suggestions.md` at the repo root** when any issue is open:

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

6. **If nothing is open**, create no file; under `full`, delete an old one
   when clean, replace it when not.
7. **Never commit or stage `suggestions.md`**; say so before a ship.

## Report (chat)

End with: mode (branch/base or local); `suggestions.md` present at start;
counts by status; its path, or deleted / not created; top open findings.

Never fix findings unless asked after the review (why:
docs/commands/review-branch.md, *Report (chat)*).
