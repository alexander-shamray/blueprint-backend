# ADR-209 — NuGet's audit warns on every restore and fails only the scheduled audit and a change that could move a package (#2010), amending ADR-019

**Decision.** `Directory.Build.props` lists NuGet's audit codes, NU1901 to
NU1904, in `WarningsNotAsErrors` unless the property `NuGetAuditAsError` is
`true`, so a restore hears an advisory as a warning and a build under
[ADR-019](ADR-019-warnings-are-errors-and-the-editorconfig-is-a-build-input.md)
does not fail on it. Two restores pass the property and fail as before:
`.github/workflows/nuget-audit.yml`, daily and on dispatch over the whole
solution, which files an issue or comments on the open one when it is red; and
ci.yml's `build` job, for a change that touches what its `restore-audit` filter
names — the pins, a project, props, targets or solution file, `global.json` or
a NuGet configuration in any spelling NuGet reads. Every other restore, the
integration shards', the per-commit builds', each image's and a developer's,
warns.
**Why.** An advisory is published against a package, not against a commit. On
266e955, a change to one document, three integration shards failed their
restore on NU1902 for a package no change had moved, and until a pin landed
every open pull request was red for the same reason (#1945, #2010). ADR-019
made every warning an error so that a change could not hide one behind a long
log, and that argument holds for a warning the change caused; an advisory
published between two pushes is caused by nobody's change, so failing the
change that happens to be next teaches nothing and blocks everything. The audit
still runs as an error every day and on every change that could introduce a
vulnerable version, and a failure there is an issue that stays open, which is
where the work of a new pin belongs.
**Consequences.** A new advisory can now sit on `main` for up to a day, and a
pull request merged in that window is green while the tree it lands on carries
a known vulnerability; the scheduled run's issue is the only thing that says
so, and it is only as good as somebody reading the tracker. The warning prints
in every restore meanwhile, in a log nobody reads for warnings, which is the
exact failure ADR-019 was written against. A change to anything the filter
names pays for every advisory in the solution, its own or not, and whether or
not it moved a package, so the pull request that bumps one pin, or edits one
project file, may have to bump an unrelated pin first. And the exemption
is by code, not by package: a build that reports NU1901 to NU1904 for any
reason other than an advisory is lenient too.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
