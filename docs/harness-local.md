# Harness-local: blueprint-backend

What the payload's commands name by role, filled in for this repository.
The four headings are fixed by the payload's `docs/harness-boundaries.md`,
*Managed files*; this file is the repository's own and no sync writes it.

## Documents

| Role | Path |
|---|---|
| Change contract | `docs/change-locality.md` |
| Style guide | `docs/style-guide.md` |
| Testing document | `docs/testing.md` |
| Product specification | `docs/backend-architecture/` |
| Roadmap | `docs/roadmap.md` |
| Closed records | `docs/pr-decision-log.md`, `docs/lessons.md` |
| Measurements | `docs/token-usage.md`, read through `docs/token-plan.md` |

The change contract's classes are its §3 and its procedure, the touch set
included, its §5; the class map is `.github/locality-gate/classes.yml`,
which §3 cites. The product specification is the blueprint, which
`CLAUDE.md` names as the specification the code honours.

## Ship checks

Selected by the class the PR body will carry (`docs/change-locality.md`
§5):

- **`/validate-blueprint`** after Class C, or after an edit to a file in
  that audit's scope — a chapter or appendix, `docs/roadmap.md`,
  `docs/testing.md`. It is **dispatched, never invoked**: its frontmatter
  denies `Edit` on `src/` until the turn ends. The passes are the ones
  `.claude/commands/validate-blueprint.md` *Dispatch* names, each a
  `general-purpose` agent told to edit nothing and handed that file's scope
  and its *Method* bullets as the brief.
- **`/check-links`** when the change touched links, cross-references or nav
  footers under `docs/backend-architecture/`.

A Class A runbook edit runs neither, and its PR body says so rather than
claiming a run.

The required checks, which `/ship` step 6's wait holds the list to, as the
`main` ruleset requires them: `changes`, `build`, `closure-gate`,
`locality-gate`, `commits` and `commit-gate`.

## Residuals

Two passages the managed `docs/harness-boundaries.md` no longer carries
live here, because only this repository's own commands make them true.
Their arguments are verbatim; the references that pointed at the file they
sat in were rebased so they still land here. In the callout, "This file"
and "this file" in its first and third paragraphs now name the boundaries
file, and "this one" and "this file" in its first paragraph's last two
sentences read "it"; in the mutation sweep's paragraph, "both its helpers"
now names the two its earlier paragraph listed.

> **`docs/harness-boundaries.md` is outside the blueprint tree, so nothing
> structural catches its drift.** `/check-links` checks
> `docs/backend-architecture/` and the root README's entry point into it, so
> a file anywhere else is outside its scope whatever links it, and
> `/validate-blueprint` does not name it — `testing.md` and `roadmap.md`
> are the only two of those siblings it does. The one rule in `CLAUDE.md`
> covers it, and that is all that does.
>
> **The siblings are described rather than listed on purpose.** Three copies
> of this callout named three different sets the moment two new files
> arrived, which is what a list with no code to check it against does.
>
> **A predicate is only better than a list if it is true.** These three
> callouts first said `docs/harness-boundaries.md` is in no index “like
> every document under `docs/` that is not the blueprint tree”, which is
> false twice over:
> `docs/runbooks/README.md` is an index of the runbooks beside it, and the
> root `README.md` links `docs/roadmap.md`. Scope is the durable fact and
> being unindexed never was — a false predicate is a stale list with an
> argument in front of it.

**`/mutation-sweep` is the third sweep, and the one that executes.** It
borrows the `secsweep-` worktree and both its helpers, `git-worktree-detach.sh`
and `git-worktree-drop.sh` (`docs/harness-boundaries.md`, *Normalisation,
residuals and the sweeps*), and its reports go to the worktree's path plus
`.mutation`, which `git-worktree-drop.sh` removes beside `.slices`. A
mutant's fate is its suite's verdict, so execution is the confirmation
there; what it measures is a test a suite lacks, which `/bug-sweep`'s
auditor names as not its finding, not the defect that sweep names as its
residual. The grant that buys it is `mutation-run.sh`, whose only argument
is the worktree path in the checked shape; the runner configuration and the
report reader are read beside the helper, never from the tree. The commit
still decides what its own build and tests do, and through its own
`.config/dotnet-tools.json` which Stryker build runs them, so the session
holds no `Edit`, `Write` or agent there, and the raw `dotnet` is denied so
that the helper is the one route to an execution;
[`docs/commands/mutation-sweep.md`](commands/mutation-sweep.md) argues the
rest.

## Review examples

Read beside `docs/harness/working-rules.md`, *What counts as a finding*,
which `.claude/commands/review-branch.md` cites with this section:

1. **Blueprint ↔ code drift**: samples, pins, type names, registration
   order, endpoints, credentials.
2. **Cross-chapter / `CLAUDE.md` contradictions**, or a planned tree stated
   as present; a stale restatement is one only if this branch **introduces
   or edits** it (`docs/change-locality.md` §2).
3. **Register drift**: `Directory.Packages.props` vs Appendix B vs
   `allowed-licences.txt`, a chapter pin as an `Include`/`Version` pair, or
   the licence gate failing.
4. **Deploy drift**: Compose / Helm / CI vs §14 / §15 (ports, secrets,
   service names, healthchecks).
