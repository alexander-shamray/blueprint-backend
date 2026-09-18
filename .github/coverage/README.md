# The domain-coverage reporter

**A report and not a gate**, on
[§12.9](../../docs/backend-architecture/12-test-strategy.md)'s argument: there
is no threshold, and a low figure exits zero. The quality gate is the
pipeline gate's `stages`, whose subject is whether a suite ran at all.

**The claim is the figure's correctness**: the domain layer's line coverage
over the whole run, as §12.9 asks. What the figure is measured over is
`coverage.runsettings`' filter, and `docs/testing.md` says why.

## What it reads

Every Cobertura file under the stage result directories it is given. A
stage that produced none is named individually and exits non-zero —
"nothing anywhere" and "the integration stage collected nothing" are
different defects, and the second is invisible in a total — as does a report
it cannot read, because a step that shrugs at no data prints nothing on the
day the collector stops.

## How it merges

§12.9 says why the figure is a union across stages. Two properties of the
artefacts decide how the reporter takes it:

- `lines-valid` counts the lines under `class/methods/method/lines`, not
  under `class/lines`, so the merge keys on the first;
- `--logger trx`, which the stage gate counts from, makes each test project
  write a partial attachment beside the run's merged one, so the same line
  arrives more than once.

Hits are therefore merged with `max` over a key that reproduces the
collector's own `lines-valid`, and reading an attachment twice cannot
inflate the figure. A run without the logger leaves a single file instead;
the union is correct under either layout. It has a suite for that reason:
arithmetic that is quietly wrong is worse than no figure.

## How it runs

Its suite runs in the fast job of [`ci.yml`](../workflows/ci.yml), and the
report after the two instrumented stages. `docs/testing.md` has the
invocation.
