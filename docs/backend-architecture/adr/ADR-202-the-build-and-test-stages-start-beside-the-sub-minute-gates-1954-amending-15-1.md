# ADR-202 — The build and test stages start beside the sub-minute gates (#1954), amending §15.1

**Decision.** In `ci.yml`, `build`, `integration` and `scaffold-build` need
`changes` alone, and the four checks that answer in under a minute,
`licence-gate`, `pipeline-gate`, `format` and `workflow-lint`, run beside them.
`stage-gate` needs all four as well as the two stages, so a red gate still
skips `stage-gate` and every job that needs it: `images` and `packages` on a
pull request, and the publishing jobs on `main`.
Where [§15.1](../15-cicd-deployment.md#151-pipeline) said the long jobs wait on
the lint, read: they start beside it, and §15.1 now draws and says so. Where
[§11.6](../11-identity-authorization.md#116-secrets), `docs/secrets.md` or
[ADR-023](ADR-023-the-consumer-driven-contract-is-a-linked-file-not-pact.md)
say the scan or the licence gate runs ahead of the build or of the path-filter
fork, read: beside them and never downstream of either, which is the property
§15.1 argues for.
**Why.** Each gate is under a minute of work but waits for a runner first, and
only then are the build and the six integration shards created and queued a
second time. Over the 36 newest green pull-request runs measured on 2026-11-12,
a run's start to its first `build` or `integration` job took 9.0 minutes at
p50 and 19.4 at p90, against 6.3 minutes for the longest single job; the run
took 16.6 at p50. Across the 1,000 newest CI runs to that date, a gate failed
in 23. The wait bought minutes on those 23 and on a run cancelled by a newer
push before the gates finished; it cost every pull request a second queue.
**Consequences.** The minutes #1461 saved are mostly spent again: a run whose
gate fails now pays for the build and the shards in full, since a red sibling
cancels neither, and a run cancelled by a newer push pays for them up to the
cancel. Only what needs `stage-gate` stays behind the gates. No merge rule
moves: the gates are not required checks, and a red one left the required
`build` skipped before, which a ruleset reads as passing, where now `build`
runs and must pass on its own; a red gate still fails the run, which `/ship`
reads before it merges.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
