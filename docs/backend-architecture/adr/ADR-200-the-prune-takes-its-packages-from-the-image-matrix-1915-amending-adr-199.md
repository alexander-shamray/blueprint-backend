# ADR-200 — The prune takes its packages from the image matrix (#1915), amending ADR-199

**Decision.** `tools/ci-cost/ghcr_prune.py` judges the packages `image_packages`
reads from the image matrix in `.github/workflows/ci.yml`, not every container
package of this repository as
[ADR-199](ADR-199-a-scheduled-prune-keeps-what-a-rollout-named-1900-amending-adr-071-and-adr-134.md)
has it. A package it cannot read loses nothing and fails the run, and the others
are still judged.
**Why.** The owner's package listing, which a person's token reads, answers a
job's `GITHUB_TOKEN` with HTTP 400: ADR-199's first dispatch, run 42628789737 on
2026-11-09, stopped there before judging anything. The matrix is what pushes
these packages, and on that date its 18 names equal the listing's 18.
**Consequences.** An image dropped from the matrix is never pruned again, and
its versions stay until someone deletes them by hand: the safe side, since the
prune then removes nothing it was not told about, but one that nothing reports.
Whether a job's token may read a package's versions by name was not exercised
when this was written, since a dispatch runs only `main`'s copy, so the first
dispatch after this lands is that check. `actions/delete-package-versions`
defaults to a job's token, and its README gives the repository delete access
when it is assigned the Admin role under a package's *Manage Actions access*, a
setting of each package. So the refusal ADR-199 names holds per package: the run
fails and that package keeps what the refused tag names, while the others still
delete.

**Amends**
[ADR-199](ADR-199-a-scheduled-prune-keeps-what-a-rollout-named-1900-amending-adr-071-and-adr-134.md),
whose packages are now the matrix's.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
