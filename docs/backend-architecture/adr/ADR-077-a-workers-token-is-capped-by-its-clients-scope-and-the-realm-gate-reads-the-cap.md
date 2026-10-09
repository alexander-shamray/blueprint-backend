# ADR-077 — A worker's token is capped by its client's scope, and the realm gate reads the cap

**Decision.** `shipping-worker` and `notifications-worker` set
`fullScopeAllowed` to false, and each client's one scope mapping is the grant
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
names: `orders:delivery-address` on `commerce-api`, and `view-users` on
`realm-management`. No client scope either worker holds maps a role, so
`offline_access` leaves both optional lists, and neither client defines a role
of its own. `deploy/keycloak/realm_check.py` judges all four in both realm
kinds (`check_scope_cap`), and on both workers and `web-bff` it refuses an
audience mapper, or a mapper writing `aud` or `permission`, anywhere but the
`commerce-api` scope (`check_token_writers`); the `roles` scope's
audience-resolve mapper names only the clients whose roles the token carries,
so on the workers it is left to the cap, and `web-bff` already carries the
audience every service validates. `read_admin.py` fetches what
those checks read — the client scopes with their mappers, every client's and
scope's scope mappings, and each client's own roles — and writes them under an
export's keys.
**Why.** ADR-052 left each grant to the worker's own token check because a
service account's roles live on its user, and reading users would widen the
check credential to every profile in the realm. That check runs inside the
worker; a stolen secret presented to Keycloak never meets it, and with full
scope on, any role later added to the account was issued in the token.
Keycloak keeps a role in a token only when the client, a scope it holds or its
own role list puts it in scope, and all three are client properties
`view-clients` reads. So the issuer now caps each token at the grant, and the
gate can see the cap in a deployed realm where it could not see the grant. An
audience mapper on the contact reader, or on a scope it holds, never touched
its scope lists and passed the gate; it now fails.
**Consequences.** An operator creating either worker client in a deployed
realm turns full scope off, adds the one scope mapping and removes
`offline_access`, or the next rollout is refused. The account's own roles are
still unread: an over-grant there reaches no token, and an under-grant fails
the worker's check as before. A mapped role's composites are read in §14.1's
export (`RealmImportTests`) and not in a deployed realm, so a realm that
redefined what `view-users` composes widens the cap unseen. The deployed read
gains one request for the scope list, two per client and one per scope.
Keycloak's default roles, which ADR-052 says every token carries, are no
longer in either worker's.

**Amended by [ADR-088](ADR-088-the-realm-gate-judges-a-workers-cap-expanded-and-the-audience-scopes-mappers-exactly.md)**,
which judges the cap as Keycloak expands it, reads what a mapped role
composes in a deployed realm, judges the `commerce-api` scope's own mappers,
and judges token writers on `web-app` and `mobile-app` as well.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
