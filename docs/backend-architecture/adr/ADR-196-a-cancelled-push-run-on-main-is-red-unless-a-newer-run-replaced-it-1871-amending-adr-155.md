# ADR-196 — A cancelled push run on main is red unless a newer run replaced it (#1871), amending ADR-155

**Decision.** `.github/workflows/main-red.yml` hears a `cancelled` push run too.
Its `read-cancelled` job runs `.github/pipeline-gate/read_cancelled_run.py`,
which reads the failure annotations of each job in the run that did not pass.
A run a job's annotation says hit its execution limit is filed; so is a run
nothing says was replaced, and a run whose read did not finish. A run an
annotation says gave way to a higher priority waiting request is not filed, and
nor is a run with no job at all. This adds a fourth conclusion to the three
[ADR-155](ADR-155-a-red-push-run-on-main-files-a-tracker-issue-and-is-reverted-before-it-is-fixed-1450.md)
names, and one exception to its revert first: a run cancelled by hand has no
pull request at fault, so the issue says that it owes no revert, only a fresh
run. That record is otherwise unchanged.
**Why.** A job that exceeds its `timeout-minutes` ends its run `cancelled`, not
`timed_out`, so the three conclusions read a timed-out job as a run the next
push replaced, and three `integration` runs on `main` timed out on 2026-11-02
with no issue filed. The two cases are told apart by evidence rather than by
elapsed time: each of the 68 cancelled push runs read on 2026-11-08 carried one
annotation or the other, but for one run with no jobs, replaced seventeen
seconds after it queued. Replaced is what must be read, not assumed, because the
two mistakes cost differently: the platform rewording its message files an issue
too many, where assuming replaced would lose the alert again without anything
failing. Asking whether a newer run exists was rejected: a job can time out on
`main` with a newer run already queued beside it, which is the case to hear, and
`ci.yml` gives each commit on `main` a group of its own, so nothing replaces its
runs.
**Consequences.** Cancelling the newest run on `main` by hand once it has a job
files an issue, which is accepted: nothing has tested that head. With no job, a
run cancelled by hand files nothing, since it cannot be told from a replaced
one. The read needs a checkout and `actions` and `checks` read grants, which
`file` does not hold, and it stays out of `file`'s concurrency group, so a
replaced run cannot displace a red run waiting there. Every cancelled push run
on `main` now starts one short job. `pipeline-gate alerts` holds both jobs'
conditions, because a clause dropped from them stops the alert with nothing
failing. The two messages are the platform's and no
contract holds them, so what a rewording of the execution-limit one costs is
the alert for a run with a replaced job beside it; a run with no replaced job
still files. As ADR-155 says of the filing step, `read-cancelled`'s first live
run is the first cancelled run on `main` after it merges.

**Amended by
[ADR-210](ADR-210-a-red-first-attempt-of-a-push-run-on-main-is-rerun-once-and-files-only-when-the-second-attempt-is-red-too-2018-amending-adr-155.md)**:
a run read as red here, a job at its time limit included, is no longer filed
when a newer push run of its workflow on `main` exists; the newer run is the
one heard. How a cancelled run is read is otherwise unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
