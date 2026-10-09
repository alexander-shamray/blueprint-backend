# ADR-088 — The realm gate judges a worker's cap expanded, and the audience scope's mappers exactly

**Decision.** `deploy/keycloak/realm_check.py` judges each worker's token cap
as Keycloak expands it: the roles its scope mappings put in scope, with every
role those compose, must equal the grant
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
names together with what that grant composes in §14.1's export
(`CAP_COMPOSITES`). So `view-users` brings `query-users` and `query-groups`
and nothing else, and `orders:delivery-address` brings nothing
(`check_scope_cap`). In a deployed realm `read_admin.py` reads the expansion
from Keycloak, as each worker's effective scope over the realm and over every
other client, and writes it under `effectiveScope`; in §14.1's export the
gate expands the mapped roles through the composites the export carries. The
gate also judges the `commerce-api` scope's own mappers: exactly one audience
mapper and one `permission` role mapper, each naming the client `Common.Web`'s
`AuthenticationExtensions.Audience` declares, and no other mapper
(`check_audience_scope`); the gate reads that audience out of the declaration
as it reads the lifetime. `check_token_writers` judges `web-app` and
`mobile-app` beside `shipping-worker`, `notifications-worker` and `web-bff`.
It amends [ADR-077](ADR-077-a-workers-token-is-capped-by-its-clients-scope-and-the-realm-gate-reads-the-cap.md),
which judged the cap as mapped, judged token writers on those three clients
alone, and left the scope's mappers to `RealmImportTests`, which cannot see a
deployed realm.
**Why.** Keycloak expands a mapped composite into the cap, so in a deployed
realm where `view-users` was made to compose `manage-users`, the contact
worker's token gained it while its mapping still read `view-users` alone.
Every client holding the `commerce-api` scope gains what that scope's
mappers write, and `check_token_writers` skips the scope because writing the
audience and the claim is its purpose; a hardcoded `permission` mapper added
there reached `web-app`, `mobile-app`, `web-bff` and `shipping-worker` at
once. A browser client's token reaches every service too, so a `permission`
writer on `web-app` or `mobile-app`, or on a scope only one of them holds,
was the same hole one client over. Both reads sit within `view-clients`,
established against Keycloak 26.0: the effective scope views answer in full
under it, and the client-scope list already carried each mapper's
configuration. The check credential is not widened.
**Consequences.** The deployed read gains, for each worker present, one
request for its effective realm scope and one per other client: twice the
realm's client count. The expected expansion is the export's, so a Keycloak
upgrade that recomposes `view-users` fails the gate until `CAP_COMPOSITES`
moves with the export. Still unread: the service account's own roles, as
ADR-077 left them; the rest of each `commerce-api` mapper's configuration,
such as whether it writes to the access token, which `RealmImportTests`
holds for §14.1's export alone. Read but unjudged: the mappers on any client
beyond the five, or on a scope only such a client holds. A renamed or
reshaped `Audience` declaration stops the gate rather than defaulting.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
