# ADR-078 — The realm check requires view-realm

**Decision.** `deploy/keycloak/read_admin.py` stops unless its token holds
`view-realm` on `realm-management` as well as `view-clients`
(`SETTINGS_ROLES`). It amends
[ADR-076](ADR-076-the-realm-check-refuses-a-credential-wider-than-a-read.md),
which permitted `view-realm` without requiring it because whether the realm
read needed it was not established.
**Why.** It is established now, against the pinned Keycloak 26.0 with §14.1's
realm imported: an account holding `view-clients` alone is answered a realm
representation with `accessTokenLifespan`, `revokeRefreshToken` and
`refreshTokenMaxReuse` absent, while the clients, scopes and scope mappings
read in full. The gate then failed every lifetime and rotation check on a
field nobody could read, and `docs/secrets.md` called that credential enough.
**Consequences.** A check account provisioned with `view-clients` alone, as
`docs/secrets.md` used to describe it, is refused by name at the next rollout
and scheduled run rather than failing three checks with a misleading value.
Nothing is widened: `view-realm` was already among `PERMITTED_ROLES`, and it
composes nothing.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
