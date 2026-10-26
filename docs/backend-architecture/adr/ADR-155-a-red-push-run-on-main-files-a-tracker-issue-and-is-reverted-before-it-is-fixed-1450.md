# ADR-155 — A red push run on main files a tracker issue and is reverted before it is fixed (#1450)

**Decision.** `.github/workflows/main-red.yml` runs on `workflow_run` for every
workflow a push to `main` runs, each named in its list, and when a push run of
one ends `failure`, `timed_out` or `startup_failure` it files an issue titled
`main is red: <workflow>`, labelled `bug`, `high` and `code`, or comments on the
open one with the run's URL and head commit. `pipeline-gate alerts` fails CI on
a workflow pushed on `main` that the list does not name, and on a listed name
that no such workflow carries. A red `main` is reverted first and fixed forward
second: the pull request that turned it red is reverted with
`git-revert-pr.sh`, and the revert lands before any fix.
`docs/change-locality.md` §6 states that and the rest of the merge rules as
working rules. Auto-merge is not used while the ruleset requires no status
check: with nothing to wait for it would merge before CI had run, so `/ship`'s
own wait on the checks stays the gate.
**Why.** Several agents' pull requests are open at once, and requiring a branch
to be up to date with `main` before it merges made every merge force every other
open pull request to rebase by hand, so the ruleset now allows only rebase
merges and no longer requires it. A pull request is then tested against `main`
as it stood at its last run, not the one it lands on, and two that are green
on their own can be red together; the push run on `main` is the first run that
tests them together. A red push run told only the notification of whoever
pushed, which is read once, where an issue stays open until somebody closes it
([ADR-111](ADR-111-a-red-object-storeyml-run-files-a-tracker-issue-or-comments-on-the-open-one-988-narrows-adr-110s-consequence-that-a-red-run-files-no-issue.md)'s
argument). Revert before fixing, because a fix forward takes as long as it
takes, and every branch cut from a red `main` meanwhile inherits the red; a
revert restores the last green tree in one known step, and the `PR:` trailer
makes it mechanical. The list is named because `workflow_run` matches a
workflow by its name, and a name nothing carries matches nothing without an
error, so a workflow added or renamed would go red on `main` unheard.
**Consequences.** The checks that read a pull request's body or commit range —
the locality, closure, commit and comment gates and the `commits` job — have no
subject on a push and are not run again after a merge. Four judge a property
of one pull request that a second merge cannot change; the `commits` job's can
change, since commits replayed onto a newer `main` may no longer build one by
one, and `git bisect` may meet one that does not compile. That is accepted,
because the head is built on `main` and a revert takes the whole pull request.
The `packages` job's version step is pull-request-only too, and two pull
requests raising one version both pass it; the feed refusing the second push on
`main` is the backstop
([ADR-150](ADR-150-the-assistant-packages-version-rule-is-checked-on-the-pull-request-and-their-blocks-leave-the-shared-filter-1447-amending-adr-149.md)),
and that red push run is one this alert hears. A
`workflow_run` trigger fires only from the default branch's copy of the
workflow, so the filing step cannot run on a pull request; the gate holds its
list, and its first live run is the first red `main` after it merges. The issue
is closed by hand on a green push run. Whether dropping the up-to-date rule pays
is measured over its first two weeks in #1450, not here.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
