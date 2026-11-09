# ADR-198 — The packages job runs on every run a stage reads (#1907), amending ADR-149

**Decision.** `packages` packs both packages on every run a build or test stage
reads, and on no other. In `ci.yml` it carries the job-level condition the
stages carry, `needs.changes.outputs.code != 'false'`, which is false only on a
pull request that changes nothing a stage reads (§15.1). Where
[ADR-149](ADR-149-the-ai-assistants-core-and-its-adapter-are-two-building-blocks-this-repository-publishes-as-nuget-packages-1346.md)
and [ADR-197](ADR-197-write-grants-leave-the-jobs-that-run-a-pull-requests-code-1789-amending-adr-109-and-adr-149.md)
say `packages` packs on every run once the test stages have passed, read: on
every run a stage reads. A push to `main` and a dispatch are always such runs,
so `publish-packages` finds the artefact it downloads whenever its own condition
holds.
**Why.** A change that no stage reads cannot make either package fail to pack:
a package's project and tree sit under `src/BuildingBlocks/`, which the `code`
filter reads, so a change to either is always a run a stage reads. Without the
condition the job restores and packs, about a minute, on a documentation or
workflow-only pull request that skipped the stages it needs. `packages` needs
`stage-gate`, which runs on every run and passes with its steps skipped, so the
condition is the job's own and nothing upstream gives it.
**Consequences.** A pull request that changes no code shows `packages` as
skipped, and its pull-request version check ([ADR-150](ADR-150-the-assistant-packages-version-rule-is-checked-on-the-pull-request-and-their-blocks-leave-the-shared-filter-1447-amending-adr-149.md))
does not run there; it has no package change to check. The guarantee rests on
the `code` filter not excluding `src/BuildingBlocks/`: a filter that excluded
the packages' trees would skip, on a pull request, a pack that could fail, and
the version check with it. The push is covered whatever the filter says, since
`code` is true on any event but a pull request, so `publish-packages` never
loses its artefact; a change to that filter is read against this record.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
