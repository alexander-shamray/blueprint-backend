# ADR-203 — The four sub-minute gates are required checks on main (#1959), amending ADR-202

**Decision.** `main`'s ruleset requires `licence-gate`, `pipeline-gate`,
`format` and `workflow-lint` beside the seven checks it already required, and
`/ship` step 6's wait holds its list to the same eleven
(`docs/harness-local.md`, *Ship checks*). Each is required by its own name,
not through a job that needs all four. The data is blueprint-harness's
`rulesets/main.json`, applied with its `ruleset-apply`. Where
[ADR-202](ADR-202-the-build-and-test-stages-start-beside-the-sub-minute-gates-1954-amending-15-1.md)
says the gates are not required checks, read: they are.
**Why.** A ruleset blocks a merge only on a check it names, and a skipped
required check counts as passing. While `build` needed the gates, a red one
left `build` skipped, so the ruleset admitted the merge; since ADR-202 `build`
runs beside them and passes on its own. Either way a red secret scan, a
malformed workflow or an unformatted file could be merged by hand, and only
`/ship`'s read of the whole run stood in the way. The four jobs carry no `if:`
and need nothing, so they run on every pull request and never skip, which is
what makes a required name safe for them. One aggregating job that needs them
was rejected: a red gate skips a job that needs it, as it skips `stage-gate`,
so the aggregator would need `if: always()` and a step reading each result,
a fifth job and runner on every run to report what the four already report.
**Consequences.** A pull request cannot merge while any of the four is red or
missing, the owner's merge by hand included. `workflow-lint` downloads
actionlint, so an outage of that release download now holds every merge, not
only the run's colour. A gate added later is not required until
`rulesets/main.json` names it, and not waited for by `/ship` until *Ship
checks* does. A gate renamed leaves its old name required and never reported,
so every pull request is held as missing that check, and step 6's wait fails,
until both lists name the new one; the harness's `rulesets` check compares the
live ruleset with that file, not with `ci.yml`.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
