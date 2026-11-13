# ADR-019 — Warnings are errors, and the .editorconfig is a build input

**Decision.** `Directory.Build.props` sets `TreatWarningsAsErrors`,
`EnforceCodeStyleInBuild` and `AnalysisLevel latest-Recommended` from PR-01, and
takes no StyleCop package. Three code-style rules are configured at `warning`
and are therefore enforced — IDE0055 formatting, IDE0065 `using` placement,
IDE0161 file-scoped namespaces. Everything else in `.editorconfig` stays a
suggestion.
**Why.** [§4.1](../04-solution-structure.md) commits to shared MSBuild settings
without saying what goes in them, and the answer does not get cheaper by
waiting: adopted at PR-01 the policy costs half a day against an empty
repository, adopted at PR-20 it costs a sweep across twenty pull requests
written without it. `TreatWarningsAsErrors` is what makes the other two settings
mean anything — a violation that only prints is a violation best hidden by the
longest build log, which is always the pull request that introduced the most of
them. StyleCop is declined because it restates rules `.editorconfig` already
carries and contradicts several of them outright, and a house style policed by
two tools that disagree is policed by neither.
**Consequences.** A compiler or analyser warning stops the build, so an SDK bump
can turn a clean tree red. That is the `global.json` pin (§4.4) earning its
place rather than an argument against the policy. Suppressions are not available
inline — `#pragma` is forbidden, so a warranted one goes in
`Directory.Build.props` with a comment saying why. Style rules whose exceptions
Roslyn cannot express stay at `suggestion` and remain a review matter: the four
cases that keep `var` are the live example, and a rule whose carve-out lives in
prose must not fail a build that cannot read the prose.

**Amended by
[ADR-209](ADR-209-nugets-audit-warns-on-every-restore-and-fails-only-the-scheduled-audit-and-a-change-that-moves-a-package-2010-amending-adr-019.md)**:
NuGet's audit codes, NU1901 to NU1904, are not errors in a restore unless
`NuGetAuditAsError` is `true`, as the scheduled audit and ci.yml's build for a
change that could move a package set it; an advisory published between two
pushes is an issue rather than a failed build.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
