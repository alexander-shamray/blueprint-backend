# ADR-076 — The realm check refuses a credential wider than a read

**Decision.** `deploy/keycloak/read_admin.py` stops before it asks for a
realm's client list unless its token holds `view-clients` on
`realm-management`, and stops as well when the token holds any
`realm-management` role outside `PERMITTED_ROLES`, the read roles that file
declares. `realm-admin` composes `view-clients` and
is refused all the same, so it no longer satisfies the completeness premise
[ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md) names it
beside; `view-realm` is permitted and is still not enough on its own.
**Why.** The credential's secret lives in the `production` Environment and is
exercised from a CI runner at every rollout and, since
[ADR-043](ADR-043-the-deployed-realm-is-checked-between-rollouts.md), hourly.
A check that refused a grant too narrow and accepted one too wide made
`docs/secrets.md`'s "realm-read rights and nothing else" a provisioning rule
nothing checked, and a leak of an over-granted credential is the identity
provider handed over whole rather than a read of it.
**Consequences.** An operator who provisioned the check account with
`realm-admin` because it works finds the next rollout and the next scheduled
check failing until the account is narrowed. The allow-list is a claim about
Keycloak's role model, so the suite asserts against §14.1's export that no
permitted role composes one outside the set and that what `view-clients`
composes is permitted; a Keycloak that reorganises those roles fails a test
rather than this check. Whether the realm read needs `view-realm` to see the
token settings at all is not established here, which is why it is permitted
rather than required.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
