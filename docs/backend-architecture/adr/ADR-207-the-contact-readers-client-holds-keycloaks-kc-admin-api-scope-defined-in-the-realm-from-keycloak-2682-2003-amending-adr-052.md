# ADR-207 — The contact reader's client holds Keycloak's kc-admin-api scope, defined in the realm, from Keycloak 26.8.2 (#2003), amending ADR-052

**Decision.** The `notifications-worker` client holds Keycloak's own
`kc-admin-api` client scope as a **default** scope, and §14.1's realm export
defines that scope itself, with its one mapper of type
`oidc-admin-api-audience-mapper`, rather than naming the built-in. The realm
gate refuses a realm whose contact client lacks the scope as a default, or
whose `kc-admin-api` scope carries no such mapper. Keycloak 26.8.2 is the
platform's floor: the Compose stack pins it, and a deployed realm runs on it
or later. In a deployed realm the scope is Keycloak's built-in, and the
operator assigns it to the contact client and to the service account
`read_admin.py` signs in with ([ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md)),
since the upgrade assigns it only to Keycloak's own admin clients. This also
amends [ADR-077](ADR-077-a-workers-token-is-capped-by-its-clients-scope-and-the-realm-gate-reads-the-cap.md):
beside the `roles` scope's audience-resolve mapper, which ADR-077 leaves to
the cap, that scope's mapper is the other writer of `aud` outside
`commerce-api` a worker may hold. `check_token_writers` judges the
`oidc-audience-mapper` type and a mapper naming `aud` or `permission`, so it
passes this one, which is of another type and names no claim, and leaving it
so is safe because the audience it writes is the admin API's, which no
service validates.
**Why.** Keycloak 26.8.2 fixes CVE-2026-19688: its admin API now
refuses with 401 a bearer token that lacks the admin API's audience, which
that scope's mapper writes. [ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s
contact read is a call to that API, so without the scope every read is
refused and no notification is sent. A realm import that declares
`clientScopes` creates none of the default built-ins (§11.5), and Keycloak
adds this one by a step of its own only after the clients are imported, so a
client naming it is imported without it and Keycloak logs that it ignored the
reference; the scope is defined in the export for that reason. The worker
requests only `roles` by name, so an optional scope never reaches its token.
The release carries twenty-five other security fixes, so staying on 26.8.1 is
not an option.
**Consequences.** The contact token now also carries the admin API's
audience, `<server>/admin/realms/<realm>`, beside the `realm-management` one
the `roles` scope already wrote; no service of this platform validates
either, so §11.5's argument that a stolen secret calls no service still
holds. It does not widen the grant either: the admin API still authorises
by `view-users` alone. A deployed Keycloak older than 26.8.2 has no such
mapper, so the realm gate, which now asks for it, fails a rollout onto one
until Keycloak is upgraded. The export defines a scope Keycloak also ships,
so a future change to the built-in's mapper is one this file does not follow
until it is edited by hand; the gate checks the mapper's type, not its
configuration. A deployed realm missed by the operator step fails the realm
gate at the next rollout or scheduled run, and a reader account missed by it
fails that gate's own read with 401; until then the contact reads fail with
401, which the *Contact read refused* alert pages on.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
