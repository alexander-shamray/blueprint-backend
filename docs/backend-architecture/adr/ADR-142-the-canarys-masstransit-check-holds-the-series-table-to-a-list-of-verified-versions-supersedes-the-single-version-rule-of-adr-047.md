# ADR-142 — The canary's MassTransit check holds the series table to a list of verified versions; supersedes the single-version rule of ADR-047

**Supersedes.** The single-version rule in
[ADR-047](ADR-047-the-canary-judges-each-workload-on-the-signals-it-receives.md):
that the table of exported series is "verified against the pinned MassTransit
version, and the check fails when the pin moves", and that an upgrade "is a red
gate until somebody re-reads the package and moves the verified version".
**Decision.** `deploy/canary/repo_gate.py` records the MassTransit versions the
series table was read from as a list, `MASSTRANSIT_VERIFIED`, and check 5 fails
when the pin in `Directory.Packages.props` is none of them. A version is added
to the list by reading that package's instrument names and units, in a change
of its own that lands before the pin moves. The list is not a second owner of
the pin, on ADR-047's terms, and a pin outside it still fails.
**Why.** Dependabot's bump edits the pin alone, so with one verified version it
was red on arrival and no commit on its branch could turn it green without
becoming a pull request of two classes that Dependabot would then no longer
rebase. Listing the verified versions lets the reading happen first, once, by a
person, and lets the bump itself pass. The check still guards what ADR-047
named: an upgrade that renames or re-units an instrument leaves every
consume- or saga-judged rung reading an absent series, and an unread version is
refused as before.
**Consequences.** The list only grows, so a version stays accepted after the
pin leaves it; that is deliberate, because a table that was read against a
version is still true of that version, but it means the list is a record and
not a statement of what is pinned. The gate trusts that whoever adds a version
has read the package: it checks membership, not the reading. The version added
with this record was added on a comparison of the user-string heaps of its
assembly and the previously verified one, which differ by three entries, two
build paths and a job-state format string against an environment variable name,
and carry the six instrument names and the units `ea` and `ms` in both; no
backend ran it, on the same terms as ADR-047's unverified Prometheus spelling.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
