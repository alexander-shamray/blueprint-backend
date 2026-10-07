# /review-branch — the argument

This file holds why `/review-branch` is shaped the way it is; the runbook is
`.claude/commands/review-branch.md`, and it wins where they differ. Read this
when the command is disputed or edited, not when it runs.

## Mode selection (do this first)

**Before choosing a mode, check whether `suggestions.md` exists at the repo
root.** That file is the running record of open issues; when it is present,
the default job is to re-verify it, not to ignore it and start a fresh review.

`recheck` as an explicit argument is kept for clarity; it is **not** required
when the file is already there. Prefer the file’s presence over the empty
argument list.

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
3. **Register drift** — `Directory.Packages.props` vs Appendix B vs
   `allowed-licences.txt`, and any chapter writing a pin as an MSBuild
   `Include`/`Version` pair, which §2 gives one owner; licence gate failing.
4. **Deploy drift** — Compose / Helm / CI vs §14 / §15 claims (ports,
   secrets, service names, healthchecks).
5. **Incomplete reconciliation** — a rule this change states (or a fix it
   claims) that the corpus still violates in the same change set.
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
   nothing or cannot run — a `--local` review with no PR yet, or a body
   carrying neither row — say so and skip this check rather than inferring
   a class. The plan's locality gate is the enforcement; this is the early
   read. **The verdict narrows and grants nothing**: an `inside` line is not
   a licence for anything this command's grant refuses, and the class's tree
   set in the contract still bounds what a row may declare.

Reject as non-findings the house styles `docs/style-guide.md` tabulates on
purpose (braceless single statements, file-scoped namespaces, explicit
types, British prose beside real identifier spellings, unpinned Aspire with
§4.4 carve-outs, spread-over-`.ToArray()` when the corpus is already
clean), and the stale restatements `docs/change-locality.md` §2 leaves in
place — a pre-existing count, "since PR-NN" or second copy of a value that
this branch did not touch, where the owner site is already correct.

Raise no finding that asks a true comment keeping `docs/style-guide.md`'s
*Comments* rules to be reworded, completed or expanded: that section
accepts a finding against a comment only when the comment is false or
breaks one of its rules, and closes it by cutting.

On a branch whose diff lies wholly under `docs/superpowers/`, raise only
what `.claude/commands/ship.md`'s *A plan is reviewed once, for
contradiction* admits: that section owns the bar for a plan, and it is
narrower than this one.

## Full review mode

**Read the change.** Prefer full source of load-bearing files over the
diff alone. Grep `src/`, `tests/` and `deploy/` for every **symbol** the
change touches, and the one chapter section that owns a rule the change
moved. Do not tour the corpus for the value: a restatement outside the
touch set that the change left stale is not this branch's to fix
(`docs/change-locality.md` §2).

## Report (chat)

**The runbook's rule never to fix findings unless asked after the review is
enforced, not prose alone.** A "do not fix" claim resting on prose while
the grant holds `Write` and `Edit` over every path `.claude/settings.json`
does not deny is, for a review command, the worse failure. The
frontmatter's `disallowed-tools` path-scopes `Edit` away from every tracked
tree, `docs/` included, **and from every tracked file at the repository
root** — denying directories alone would leave `CLAUDE.md`,
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

**One limit, stated rather than glossed.** The list is a deny-list, so a tree
added later is editable until someone adds it. `test_harness_denies.py` asserts
the list covers every tracked top-level tree, which is what makes that a red
build instead of a quiet widening.

The rest of this argument — that the deny-list cannot see a file that does
not exist yet, that MSBuild auto-imports `Directory.Build.targets`, why the
executor is `dotnet-test.sh` rather than a raw `dotnet test:*` grant, why the
auto-imported names are denied, and why the exact names rather than the `**/`
globs are the control — is owned by `docs/harness-boundaries.md`, *Agent-type,
push and dotnet entries*.
