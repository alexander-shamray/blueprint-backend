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

`/new-adr` and `/new-chapter` are not checks, and step 2 runs neither. A
`/ship` turn runs them before its push when the change records a decision or
adds a chapter, and a frontmatter deny holds for the rest of the turn
(`docs/harness-boundaries.md`, *Sweep push denies and chained commands*), so
neither may deny push; `.claude/scripts/local_commands.py` declares them
beside the two checks above.

The required checks, which `/ship` step 6's wait holds the list to, as the
`main` ruleset requires them: `changes`, `build`, `closure-gate`,
`locality-gate`, `commits` and `commit-gate`.

## Residuals

Three passages the managed `docs/harness-boundaries.md` no longer carries
live here, because only this repository's own commands make them true.
Their arguments are verbatim; the references that pointed at the file they
sat in were rebased so they still land here. In the callout, "This file"
and "this file" in its first and third paragraphs now name the boundaries
file, and "this one" and "this file" in its first paragraph's last two
sentences read "it"; in the mutation sweep's paragraph, "both its helpers"
now names the two its earlier paragraph listed; in the build tool's
passage, "The ninth" now names the boundaries file's entry that hands it
here, and "backend's" reads "this repository's".

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

**The ninth entry of `docs/harness-boundaries.md`, *Agent-type and push
entries*, is this repository's branch-review command's pair of `dotnet`
grants, and what makes it worth its own entry is that the enumeration
guarding it could never have worked.** That command holds `Write` and held
`Bash(dotnet build:*)` and `Bash(dotnet test:*)`; its `disallowed-tools`
denies every tracked root file, read from `git ls-files` by a test so a new
one is a red build. But MSBuild imports `Directory.Build.targets` into every
build of every project beneath it, and that file **does not exist in this
repository** — so it is on no list read from what exists, and no care about
that test could have put it there. Write it, run the build the command
already had, and an `Exec` in it runs. Measured, in a scratch tree: the
target fires and `dotnet build` reports success. Raised in review.

`dotnet build` was a grant the command never used — its body names
`dotnet test` alone — and `dotnet test:*` bought two things beyond the run it
was added for: any project path, and `/p:CustomBeforeMicrosoftCommonTargets=`,
which imports whatever file it names, `suggestions.md` among them. Both are
gone, replaced by `dotnet-test.sh`, whose only variable is one word out of two.
**The helper closes the executor and not the import**, so the auto-imported
names are denied as well — in **both** commands that state editing boundaries,
because the artefact outlives the command that writes it and only one of the
two had an executor. A `Directory.Build.targets` written by this repository's
blueprint audit is executed by the next thing that builds.

**Which half is measured is recorded rather than rounded up.** The exact
filenames are the spelling both files already used and the suite reads. The
`**/*.targets`-style globs beside them are the documented gitignore syntax and
are **not** measured in a `disallowed-tools` value — belt to the names' braces,
and not the control.

**The `/validate-blueprint` passes `/ship` step 2 dispatches hold `Edit`,
`Write` and `Bash`.** *Ship checks* spawns them as `general-purpose` agents
told to edit nothing rather than invoking the command, so the `src/` and
`tests/` denies in its frontmatter never load
([`docs/commands/validate-blueprint.md`](commands/validate-blueprint.md),
*Do not*). A paragraph planted in a chapter on the branch under review could
steer a pass to edit source, or to return a finding that step 2 then
reconciles into source. What still stands in the way: the planting needs
commit access to the branch, the brief says to edit nothing, and step 5's
review reads the branch's diff, a planted edit included, before step 6 merges.
The fix is a read-only profile with `Read`, `Grep` and `Glob`, as
`bug-auditor` has, dispatched in place of `general-purpose`; it is a change
under `.claude/` and in the harness payload's `/ship`, which #1786 tracks, and
this residual goes when it lands.

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
