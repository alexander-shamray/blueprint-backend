# ADR-052 — A contact is read from its owner by a worker, and kept in the reader's own table

**Decision.** Shipping obtains a delivery address, and Notifications a
mailbox and a locale, by a **synchronous read of the owner** — Ordering,
which holds `Order.ShippingAddress`, and Keycloak, which is the only holder
of an email address the platform has. This is the question
[ADR-035](ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
left to Shipping's own pull request, asked a second time by Notifications,
and answered once.

**No consumer makes the read.** A consumer writes a row in its own
transaction — a `Shipment` awaiting its address, a notification awaiting its
send — and acknowledges the message. A background worker claims that row
under a lease that expires without its owner, as `OutboxDispatcher` claims a
batch, makes the read, and commits the answer. So
[ADR-017](ADR-017-one-synchronous-hop.md)'s exception for a call inside a
consumer is **not spent**: no message waits on another service, no endpoint's
concurrency slot is held across a hop, and an owner that is down stops a
worker's progress and nothing else.

**The answer is kept in a table in the reader's own database**, never in
Redis: [ADR-006](ADR-006-redis-for-cache-and-coordination-never-as-a-store-of-record.md)
forbids a load-bearing value there, and a TTL expires during exactly the
outage the copy exists to survive. Shipping's contact row is keyed by the
order and holds the address and the customer's id; Notifications' is keyed
by the customer and holds the mailbox, the locale and the instant it was
fetched — and **nothing else the owner offered**, because Keycloak's user
representation carries a name and attributes nobody asked for. **An absent
locale is an answer and not a fault**: the realm ships with
internationalisation off, so today every answer lacks one, and what such a
customer is sent is decided with Notifications' languages and not here.

Two rows are in play and the table keeps them apart: the **contact row**
is the copy, and the **work's row** is the shipment or the notification
waiting on it. Every read has one of five outcomes:

| The contact row | The owner, or the token endpoint on the way to it | The worker |
|---|---|---|
| present and fresh | not asked | proceeds; no call is made |
| absent or no longer fresh | answers with the value | stores the answer, proceeds |
| absent or no longer fresh | unreachable, or failing as a server does — the token endpoint included | proceeds on a contact row younger than the stale ceiling; otherwise leaves the work **where it already is, in the work's row**, for a later pass with a backoff |
| absent or no longer fresh | refuses the credential — the token endpoint refusing the client, the client's own check refusing the token, or the owner refusing the call | never proceeds on a stale row; leaves the work in its row with a backoff, and logs and counts a defect, because a revoked grant is somebody's decision and not an outage |
| absent or no longer fresh | answers that the value does not exist | records a terminal outcome on the work's row, deletes the contact row, and stops; nothing is retried |

**Waiting has an end, and it is a value of the deployment.** Work that has
waited past its service's give-up age takes the fifth row's terminal
outcome with a reason of its own, because a notification a day late is
worse than none and a row retried for ever is a defect nobody is shown.
Shipping's age is sized to reach `OrderFulfilmentSaga.DespatchTimeoutDelay`,
past which the saga has already raised the order for review.

**"Does not exist" is wider than a missing record.** For Ordering it is no
such order, an order that is cancelled, and an order whose address erasure
has cleared, and Ordering answers all three `NotFound`, so the client maps
a status and never reads an order's state.
For Keycloak it is no such user, a user with no email, and **a disabled
user** — an account taken back from somebody is most often that, and
Keycloak still answers with its mailbox. An unverified email is not one of
them: the realm ships with `verifyEmail` off, so the flag says nothing here.

**The freshness numbers are two, they are Notifications', and they are
settings that refuse.** A mailbox younger than `ContactOptions.Freshness` —
fifteen minutes — is served without a call; past it the owner is asked, and
only while the owner cannot answer is an older row served, up to
`ContactOptions.StaleCeiling`, twenty-four hours. Start-up refuses a ceiling
below the freshness rather than clamping it, as `RetentionPolicy` does.
Shipping has neither number: Ordering exposes nothing that changes an order's
address after it is placed, so an address once read is never stale.

**Two credentials are minted, and each is sized by what it reads when it is
stolen.** Shipping's client holds one client role on `commerce-api`,
`orders:delivery-address`, and takes `commerce-api` as a **default** client
scope, as §11.5 requires of `web-bff`, since the `permission` claim exists
only through that scope's mapper. Ordering serves it as a gRPC method,
`DeliveryAddresses.Get`, because [§9.7](../09-messaging.md) makes gRPC the
transport between services and this is the second such call. The method
requires that permission, **skips the ownership check on purpose** — a service
account's subject owns no order — and answers the address and nothing else. A
gRPC method's path is `/<package>.<service>/<method>` and it is served on a
second, HTTP/2-only port, as §9.7 asks of Catalog's; no route in the gateway's
file matches that path and no cluster dials that port, and a gateway test says
so. Notifications' client does not take that scope, and holds `view-users` on
`realm-management` — with the two query roles that role composes, and nothing
else — the narrowest grant the pinned Keycloak offers without a preview
feature; a stolen secret reads every user's profile in the realm and no
credential.

**Each client holds itself to its grant, because the realm gate cannot.**
[ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md)'s check
reads the realm and its client list, and a service account's roles are in
neither document; reading them would widen the realm-check credential past
the `view-clients`-only grant `docs/secrets.md` argues for. So the token
client reads, out of the access token it was issued, the one claim its grant
lives in — `permission` for Shipping, `resource_access.realm-management.roles`
for Notifications — and refuses a token whose set there is not exactly the one
this record names. `read_admin.py` reads the same claim of its own token for a
floor; this is the ceiling beside it. Keycloak's default roles, in
`realm_access` and on the `account` client, are in every token the realm
issues and are outside the check, named here so that nobody widens it to them.
A realm that
granted more in that claim fails the worker loudly in any environment,
where a wider gate would have had to be trusted with more to say so; a grant
on some other client is outside the check, and each secret's provisioning
note in `docs/secrets.md` is what says it must not exist. Each secret takes
a rotation row in `docs/secrets.md`, and each client is proved both ways
against a token Keycloak issued: accepted with the grant, refused without.

**Neither table outlives its use.** Shipping's address is a table of its
own beside `Shipments`, not columns on it, and carries the customer's id
for erasure's sake alone. Shipping deletes a contact row a retention window
after its shipment reaches a terminal state, Notifications deletes one not
refreshed for its window, and both windows are deployment values.
[§11.7](../11-identity-authorization.md)'s erasure consumer — owed with
that extension, like the rest of it — deletes the subject's contact rows,
which is that section's *delete* and leaves the shipment's own record
whole, **and ends the subject's waiting work** — deleted where §11.7
deletes the record, as Notifications' is, and marked terminal where the
record is kept, as Shipping's is — because a worker that finds no contact
row asks the owner again. For the same reason **a reader is erased after
its owner**: Keycloak is no participant in §11.7's choreography, and
Ordering is one with no order against Shipping, so a broadcast that reaches
the reader first leaves a value to be re-read by the next event for that
customer. The extension sequences the two, and §11.7's diagram, which draws
one simultaneous broadcast, is amended with it. All of this is owed with
the extension and stated here so that it is designed against.

**Why.** The address and the mailbox cannot arrive by event — ADR-035 took
the one off the bus and the other was never on it — and no service may read
another's database. What is left is a call, and the only open question is
who pays for it being slow or down.

ADR-035 declined to choose "before a consumer exists to state what it
needs". Two now do, and they ask the same question; deciding
it in each service's own pull request is how the platform comes to hold two
answers to it.

A call inside the consumer makes the message pay. `UseMessageRetry` holds
the delivery and the endpoint's slot for its whole ladder, so a dead owner
is met by every message separately and ends in `_error`, where §13.6 pages
at one message for an outage that is nobody's defect. Parking the message
in ADR-021's delayed exchange instead trusts a store that
[ADR-021](ADR-021-saga-timeouts-are-scheduled-by-the-broker.md) itself
describes as per-node and unreplicated, and names a standing population
there as its own supersession trigger — Shipping's deadline is
`OrderFulfilmentSaga.DespatchTimeoutDelay`, which is days. A row is the
store this platform already trusts to remember, an operator can select from
it, and both services need the row anyway: Shipping's is the shipment, and
Notifications owes a record of intent before a send that no key can make
exactly-once.

Serving a stale mailbox is the failure chosen, because a notification to
the last known mailbox beats silence during an identity-provider outage.
**The ceiling is a security number as well as an availability one**: an
email changed at Keycloak because an account was taken back from somebody
is a mailbox that goes on receiving order details until the row is
refreshed. Freshness bounds that to fifteen minutes while Keycloak answers
and the ceiling to a day while it does not, which is
[ADR-033](ADR-033-revocation-is-bounded-by-the-token-lifetime-and-no-denylist-exists.md)'s
form — a figure somebody chose, with what shortens it: the setting, or an
operator deleting the row.

**This departs from four sentences that would otherwise forbid it, and says
so.** ADR-017's consequence is
that cross-context data "must arrive by event and be projected locally";
for these two values ADR-035 closed that road, so they are projected
locally and fetched. ADR-035 expected the read to be "recorded as the
ADR-017 exception a second synchronous hop has to be"; moving the call out
of the consumer is what answers that, since the exception exists for a
message that waits and none does. And
[§2.3](../02-architecture-at-a-glance.md)'s fourth principle —
synchronously "only when a user is waiting on the answer" — gains its one
stated departure: a worker waiting on a row nobody is watching. And
[§9.1](../09-messaging.md) says Shipping "should not call back" for an
address, which was the argument for a field ADR-035 then removed; Shipping
does call back, from a worker. Each of the four carries a callout naming
this record.

**Consequences.** **[§11.5](../11-identity-authorization.md)'s count goes
from one to three.** That section makes the number of hosts holding a client
secret the number of synchronous couplings in the platform and says both are
meant to stay at one; a callout there now names this record. The BFF's
`Identity/` types — the token cache, the handler, the options — move to a
building block with the first of the two services, since a third copy of the
code that posts a client secret is the wrong thing to have three of.

**Notifications, the service
[ADR-036](ADR-036-the-broker-has-a-per-service-identity.md) picked as the
estate's least valuable, holds a grant that reads every user.** No narrower
one exists on the pinned image; the client's own check holds it to exactly
that, and the alternative — Keycloak pushing profile changes onto the bus — is
an identity-provider extension this platform does not have and would put a
mailbox on the wire ADR-035 cleared.

**Nobody on the platform notices a stale mailbox.** The customer does, by
not receiving mail at the new one. The window is stated because it cannot be
observed.

**The work is slower by a tick.** A shipment waits for the worker's next
pass before its address is read, where an inline call would have made it at
once; §13.7's event target is about a consumer starting, which this leaves
alone, and the tick is a named constant beside the hop's budget.

**A cold table meets a burst once per replica.** A worker resolves a
customer once per pass, so concurrent reads of one customer are bounded by
the replica count and not by the number of messages. That is accepted with
that number rather than closed with §8.1's lock, which would give a service
§2 draws with no Redis edge a reason to grow one.

**Two more tables hold personal data**, and ADR-035's residual — "a consumer
that independently resolves `CustomerId` re-creates the problem in its own
store" — is now true of two stores by decision. What keeps it tractable is
that each is one table, named, with a window and an erasure path written
beside its creation, and that neither value is ever a log attribute or an
exception's text.

**The pull request that builds each service amends the places that say the
BFF is alone**, as ADR-051 left its list to the pull request that builds
it. The list is written out for ADR-051's reason, and the rows marked
**asserted** are gates that go red, which a builder must not learn from CI:

| Owner | What it says today |
|---|---|
| [§2.2](../02-architecture-at-a-glance.md) | One synchronous edge, drawn from the BFF |
| [§3.2](../03-bounded-contexts.md) | Rows for Shipping and Notifications that reach nothing but the broker |
| [§4.1](../04-solution-structure.md) | The BFF as "the ONLY host that calls a service", and no building block holding the identity types |
| [§9.7](../09-messaging.md) | The pricing hop as the platform's one synchronous call between its services, and `Web.Bff` as "the only one holding client credentials" |
| [§11.5](../11-identity-authorization.md) | One host in the table of realm objects, and "the platform's only permitted synchronous hop" |
| [§12](../12-test-strategy.md) and [ADR-023](ADR-023-the-consumer-driven-contract-is-a-linked-file-not-pact.md) | One suite that runs a real Keycloak, for one client; and a linked-file contract that "covers one relationship", so whether `DeliveryAddresses.Get` earns a second is judged with Shipping |
| [§14.1](../14-local-development.md) and [§14.2](../14-local-development.md) | One client secret among the Compose defaults, and the BFF as "the only host that calls a peer synchronously" |
| [§15.1](../15-cicd-deployment.md) and [§15.4](../15-cicd-deployment.md) | "one client secret in the whole platform", and three inventory rows marked BFF only |
| `deploy/helm/common/templates/_helpers.tpl` | `identity.clientCredentials` refused on any chart but `web-bff`, by a `fail` that names the chart — **asserted**: a second credentialed chart does not render until that comparison moves |
| `deploy/helm/smoke.sh` | "exactly one chart declares client credentials", "exactly one workload in the platform holds a client secret", "and it is the BFF", and a loop requiring every other chart to refuse the key — all **asserted** |
| `tests/Web.Bff.Tests/RealmClientTests.cs` | `It_is_the_only_service_account_client_in_the_realm`, **asserted** over the realm export: the first new client turns it red until the expected set names it |
| `tests/Common.Web.Tests/RealmImportTests.cs` | `No_client_ships_a_secret_but_the_one_whose_grant_needs_one`, **asserted**: any client but `web-bff` that ships a secret turns it red until the credentialed set names it. And `The_permission_vocabulary_is_a_closed_set_of_client_roles`, **asserted** as the whole set: `orders:delivery-address` turns it red until the list names it |
| `tests/Web.Bff.Tests/KeycloakIdentityTests.cs` | A comment that the permission vocabulary "belongs to people, not to hosts", which is why Catalog's gRPC service asks for authentication and no permission. Shipping's client is the first host to hold one, because its read crosses subjects and an authenticated caller alone would let the BFF's client make it |
| `deploy/helm/catalog/` | "the platform's one gRPC server" in `Chart.yaml`; in `values.yaml`, the second port as "the BFF's one synchronous hop" |
| `deploy/compose/README.md` | The BFF as "the only host that mints one of its own" |
| `docs/runbooks/latency.md` | "exactly one synchronous hop in this platform" |
| `src/BFF/Web.Bff/` and `tools/new-service/scaffold/render.py` | Comments that call the BFF the one credentialed host and the one synchronous caller |
| [§11.7](../11-identity-authorization.md) | One simultaneous erasure broadcast, with no reader sequenced after its owner, and Shipping's step drawn as anonymising a recipient on the `Shipment` |
| `docs/secrets.md` | The BFF as "the only host that calls a peer synchronously" |
| `docs/repo-map.md` and `CLAUDE.md` | The BFF as the one synchronous caller — in `docs/repo-map.md` also the only holder of client credentials — and Catalog as the one gRPC server |
| `deploy/helm/ordering/values.yaml` | One port, and "Ordering serves no gRPC", argued from ADR-017's one hop |
| `realm-export.json` | The `web-bff` client's description as the only one holding client credentials, and a realm with internationalisation off, so no user has a locale to read |

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
