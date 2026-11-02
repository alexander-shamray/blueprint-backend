# ADR-194 — Auto-merge stays off on its own argument (#1716), amending ADR-155

**Decision.** A pull request here is not set to auto-merge, although the
repository allows it and `main`'s ruleset requires status checks: `/ship`
step 6 stays the merge, waiting on the checks itself and landing the head it
read mergeable through `gh-pr-merge.sh`. The reason
[ADR-155](ADR-155-a-red-push-run-on-main-files-a-tracker-issue-and-is-reverted-before-it-is-fixed-1450.md)
gave, that the ruleset requires no status check, is withdrawn; the rule it
stated stands on the two below.
**Why.** ADR-155's premise was false when #1715's review read it: the ruleset
`main non-writable directly`, read back through the rulesets API, requires the
six checks `/ship` step 6's wait names, so auto-merge would wait for them and
would not merge before CI had run. The rule still holds. Auto-merge lands
whatever head the branch carries when its checks pass, so a push made after it
is enabled lands although no session read that head, where `gh-pr-merge.sh`
pins `--match-head-commit` to the oid whose
mergeability was read and refuses a later head. And auto-merge trusts the
ruleset's list as it stands: a check dropped from the ruleset would stop being
waited for with nothing failing, where step 6's wait fails a list that misses
any of its six, whatever the ruleset says.
**Consequences.** A pull request whose checks turn green while no session
waits on them stays open until one runs step 6; nothing lands unattended
except through `/dispatch`'s executors, which run step 6 themselves. The
repository setting that allows auto-merge is left on, so this is a working
rule, not a setting, and the check list is held in two places, the ruleset
and step 6, with nothing comparing them: a check added only to the ruleset is
still enforced by GitHub, and one removed only from it is still waited for by
`/ship`.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
