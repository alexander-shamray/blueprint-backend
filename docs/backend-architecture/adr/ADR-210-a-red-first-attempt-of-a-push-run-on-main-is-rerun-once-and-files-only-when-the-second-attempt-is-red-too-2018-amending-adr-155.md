# ADR-210 — A red first attempt of a push run on main is rerun once, and files only when the second attempt is red too (#2018), amending ADR-155

**Decision.** When the first attempt of a push run on `main` ends red, in the
sense [ADR-155](ADR-155-a-red-push-run-on-main-files-a-tracker-issue-and-is-reverted-before-it-is-fixed-1450.md)
and [ADR-196](ADR-196-a-cancelled-push-run-on-main-is-red-unless-a-newer-run-replaced-it-1871-amending-adr-155.md)
give it, `.github/workflows/main-red.yml`'s `rerun` job runs
`.github/pipeline-gate/rerun_red_run.py`: it reads each job and step that did
not pass, waits until no newer push run of that workflow on `main` is still
going, reruns the failed jobs once, comments them on the open *Flaky tests
tracker*, then waits for the second attempt to finish. `file` files
`main is red: <workflow>` unless that job finished and said the second attempt
passed, or ended `cancelled` with a newer run named as replacing it, read as
ADR-196 reads a first attempt; so a refused rerun, a job that did not finish,
and a newer run or a second attempt not done by the script's deadline all
file at once.
An attempt past the first is filed only when a person started it, since the
`rerun` job files its own. `pipeline-gate alerts` holds `rerun` to the first
attempt and `file` to a rerun that did not say it passed. The revert-first
rule is unchanged.
**Why.** Eight of the eleven "main is red" issues this repository filed
between 2026-10-25 and 2026-11-13 closed on a rerun or the next green push with
no code change: a loaded runner or an outside service. Each one stopped
`/ship` step 6 in every session until a person reran the job, and a revert
would have undone a pull request that was not at fault. The watcher waits for
the second attempt rather than hearing it as a `workflow_run` event, because a
rerun this workflow's token starts may not deliver one, and the filing must
not rest on something nobody has confirmed. It waits for a newer run first
because most watched workflows cancel a running run of `main` when another
joins their group, so a rerun queued beside a newer run would cancel it and
leave `main`'s head untested. The tracker comment is written
before the wait, so a flake that reruns green is still counted.
**Consequences.** A red `main` reaches its issue one rerun later, the length
of the failed jobs' second attempt, and a runner is held for that long; the
repository is public, so the wait costs a concurrent job slot, not minutes.
A red run that would also fail a second time, a real break, is filed later
than before, by that same length. If GitHub ever delivers `workflow_run` for
the bot's rerun, `file` skips it on its triggering actor, so a red second
attempt is filed once, by the `rerun` job's own wait. A push landing in the
seconds between the newer-run read and the rerun can still be cancelled by
it; a push landing during the rerun cancels the rerun instead, which is read
as replaced, and the newer run is the one heard. The job's
`timeout-minutes` sits above the script's deadline; were it below, a long
rerun would still be filed, as a job that did not finish, but without its
second attempt's outcome. That pairing is held by a comment, not a gate. As
ADR-155 says of the filing step, the first live run is the first red push run
on `main` after this merges.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
