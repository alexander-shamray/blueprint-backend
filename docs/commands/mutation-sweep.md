# `/mutation-sweep` — the argument

The runbook is [`.claude/commands/mutation-sweep.md`](../../.claude/commands/mutation-sweep.md);
this file is why each of its rules is shaped the way it is, read when one is
disputed or edited. The helpers it runs carry their own reasons in their
headers, and [`docs/harness-boundaries.md`](../harness-boundaries.md),
*Normalisation, residuals and the sweeps*, owns what the grants buy.

## What this command runs, and what it hands over

[§12.9](../backend-architecture/12-test-strategy.md) asks for coverage of the
domain layer to be near-total and reports the figure without gating on it.
Line coverage says a test executed a line; it cannot say a test would notice
the line change. A mutant answers that second question directly: Stryker
rewrites one expression, runs the project's suite, and a suite that still
passes has a behaviour nobody pins. So the sweep's subject is the gap between
what the domain tests execute and what they detect, and the finding it files
is a missing test, not a defect.

That is why it hands over rather than overlaps. `/bug-sweep` files code that
is wrong against its own intent, and its auditor's profile names absent tests
as not its finding; a surviving mutant is exactly an absent test. `/ship`
closes what this files, and the issue is shaped for it: one project's tests
are one service's slice, which is Class A in
[`docs/change-locality.md`](../change-locality.md) §3.

The domain layer is the whole scope because it is where §12.9 puts the bar,
where tests run without containers, and where a run costs minutes rather than
the hours an Application or Infrastructure suite would cost per mutant.

## Why it executes, where the other sweeps read

`/bug-sweep` and `/security-sweep` confirm by reading and take no build
grant. This command cannot: a mutant's fate is the suite's verdict, and no
reading reproduces it. So it executes the pinned snapshot, through one helper
whose only argument is the worktree path, held to the shape
`git-worktree-detach.sh` makes.

What it does not do is let the audited tree choose how Stryker is
configured or how its report is read. The configuration and the report
reader are read from the caller's checkout, beside the helper, so a commit
cannot loosen the runner's settings or teach the reader to pass a broken
run. The tree still chooses what its own tests do, which is what running a
suite means, and which Stryker build runs them, because the helper restores
the tools the commit's `.config/dotnet-tools.json` pins. It also restores
each test project and names the target framework the project evaluates to,
for the reason [`docs/testing.md`](../testing.md) gives under *Mutation*;
both come from the tree, as its build already does. It is the snapshot
`/review-branch`'s `dotnet-test.sh` already runs, and the session holds no
`Edit` or `Write` here, so nothing this command does can add an MSBuild
import to it.

The build writes under `artifacts/`, which `.gitignore` covers, and
`git worktree remove` without `--force` removes a worktree holding ignored
output, measured on 2026-10-27; the reports go beside the worktree, where
`git-worktree-drop.sh` takes them.

## Why the runner is pinned to the testing platform

Stryker's default runner is VSTest. On 2026-10-27, at `33227bd`, it reported
the domain suites of six projects as having detected every mutant, and the
seventh as having detected none. The Microsoft Testing Platform runner, on
the same commit, reported mutants surviving in all seven, and one of them was
applied by hand: `Order.ConfirmStock` without its `EnsureStatus` call, and
the Ordering domain suite passed. So VSTest's clean result was false, and
this repository's suites are xunit.v3, which runs on the testing platform
natively.

The pin is in `stryker-config.json`, and the suite holds it there. The second
failure shape, every mutant through, is refused by the report reader whatever
the runner: a report in which nothing was detected says the suite never ran
against the mutated assembly, and filing from it would file every line.

## Why exception messages are not mutated

`ignore-methods` leaves the arguments of an exception's constructor alone.
The domain tests assert that an invariant throws, not the sentence it throws
with, which is the right test; mutating the message produces a survivor for
every guard in the domain, and in the Ordering domain on the same commit that
was most of what survived.

The statement that throws is still mutated: removing a guard's `throw` is the
mutant the sweep most exists to catch.

## One issue per project, at low severity

**One issue per project**, because its mutants share a cause and a fix: one
suite does not pin them, and one pull request in that project's tests closes
them. A per-mutant issue would file the same cause many times; one issue for
the whole run would cross slices and could not be closed by a Class A change.

**Low**, because the label's scale is about what the code does, and here the
code may do nothing wrong. What the issue tracks is hardening, which is what
the `low` label already says. A mutant whose survival exposes real wrong
behaviour is a defect too, and the fixing session files that under
`/bug-sweep`'s scale.

**The kind is `test-gap`**, a label of its own, because `bug` would claim a
defect the run did not establish. **The route is `mutation`**, because the
sweeps' sentence claims a second auditor's reading, which this run never had;
its evidence is an execution, and the trailer says so.

Only an open issue the owner opened blocks a re-file, on the other sweeps'
rule and through the same helper. A closed one does not, because a project
whose suite lets mutants through again has the gap back, and the new issue's
list is the current one.

## Never fail open

A refused run files nothing. Each refusal the reader makes is a shape in
which a clean result would be indistinguishable from a broken one: a domain
project with no test project would report no survivors because it ran
nothing; a missing report would report no survivors because there is nothing
to read; a report with nothing detected would file every mutant because the
suite never ran. None of them is a finding, so none of them files.

## Residuals

- **Equivalent mutants are filed.** A mutant that changes nothing a caller
  can observe survives every honest suite. The sweep cannot tell one from a
  gap without reading the code, and it reads nothing; the pull request that
  closes the issue argues each one instead of testing it.
- **No CI job runs it.** A mutation run costs minutes of build per project
  and its result is a list for a person to work, not a status; §12.9's
  argument against gating a diagnostic applies to it as to the coverage
  figure.
- **MTP is a preview runner in Stryker.** The reader's refusals cover the
  failure shapes measured above; a runner that misreports some mutants and
  not others would pass them, and the pull request that writes the test is
  where such a mutant is found to have been killed all along.
