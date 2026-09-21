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
outage the copy exists to survive. Shipping's row is keyed by the order and
holds the address; Notifications' is keyed by the customer and holds the
mailbox, the locale and the instant it was fetched — and **nothing else the
owner offered**, because Keycloak's user representation carries a name and
attributes nobody asked for. Every read has four outcomes and the worker
takes exactly one:

| The row | The owner | The worker |
|---|---|---|
| present and fresh | not asked | proceeds; no call is made |
| absent or no longer fresh | answers | stores the answer, proceeds |
| absent or no longer fresh | unreachable | proceeds on a row younger than the stale ceiling; otherwise leaves the work **where it already is, in its row**, for a later pass with a backoff |
| any | answers that there is no such order, user or mailbox | records a terminal outcome on the row and stops; nothing is retried |

**The numbers are two, they are Notifications', and they are settings that
refuse.** A mailbox younger than `ContactOptions.Freshness` — fifteen
minutes — is served without a call; past it the owner is asked, and only
while the owner cannot answer is an older row served, up to
`ContactOptions.StaleCeiling`, twenty-four hours. Start-up refuses a ceiling
below the freshness rather than clamping it, as `RetentionPolicy` does.
Shipping has neither number: Ordering exposes nothing that changes an
order's address after it is placed, so an address once read is never stale.

**Two credentials are minted, and each is sized by what it reads when it is
stolen.** Shipping's client holds one client role on `commerce-api`,
`orders:delivery-address`. Ordering serves it at
`GET /internal/v1/orders/{id}/delivery-address`, which requires that
permission, **skips the ownership check on purpose** — a service account's
subject owns no order — and answers the address and nothing else. The path
sits outside `/v1/orders` so that the gateway's catch-all route for Ordering
cannot reach it, and a gateway test says no route matches it. Notifications'
client holds `view-users` on `realm-management` — with the two query roles
that role composes, and nothing else — the narrowest grant the pinned
Keycloak offers without a preview feature; a stolen secret reads every
user's profile in the realm and no credential.

**Each client holds itself to its grant, because the realm gate cannot.**
[ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md)'s check
reads the realm and its client list, and a service account's roles are in
neither document; reading them would cost the realm-check credential
`view-users`, which `docs/secrets.md` argues it must not hold. So the token
client reads the roles out of the access token it was issued, as
`read_admin.py` reads its own, and refuses to use a token carrying any role
beyond the ones this record names — a realm that granted more fails the
worker loudly in any environment, where a wider gate would have had to be
trusted with more to say so. Each secret takes a rotation row in
`docs/secrets.md`, and each client is proved both ways against a token
Keycloak issued: accepted with the grant, refused without.

**The read never leaves the deployment**, and neither table outlives its
use. Shipping's address is a table of its own beside `Shipments`, not
columns on it, and carries the customer's id for one purpose: Shipping
deletes a row a retention window after its shipment reaches a terminal
state, Notifications deletes a contact row unread for its window, both
windows are deployment values, and
[§11.7](../11-identity-authorization.md)'s erasure consumer — owed with
that extension, like the rest of it — deletes the subject's rows in both
tables, which is that section's *delete* and leaves the shipment's own
record whole.

**Why.** The address and the mailbox cannot arrive by event — ADR-035 took
the one off the bus and the other was never on it — and no service may read
another's database. What is left is a call, and the only open question is
who pays for it being slow or down.

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

**This departs from three sentences and says so.** ADR-017's consequence is
that cross-context data "must arrive by event and be projected locally";
for these two values ADR-035 closed that road, so they are projected
locally and fetched. ADR-035 expected the read to be "recorded as the
ADR-017 exception a second synchronous hop has to be"; moving the call out
of the consumer is what answers that, since the exception exists for a
message that waits and none does. And
[§2.3](../02-architecture-at-a-glance.md)'s fourth principle —
synchronously "only when a user is waiting on the answer" — gains its one
stated departure: a worker waiting on a row nobody is watching. Each of the
three carries a callout naming this record.

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
one exists on the pinned image; the realm check holds it to exactly that,
and the alternative — Keycloak pushing profile changes onto the bus — is an
identity-provider extension this platform does not have and would put a
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

**The pull request that builds each service amends every place that says
the BFF is alone**, as ADR-051 left its list to the pull request that
builds it, and the list is written out for ADR-051's reason:

| Owner | What it says today |
|---|---|
| [§2.2](../02-architecture-at-a-glance.md) | One synchronous edge, drawn from the BFF |
| [§3.2](../03-bounded-contexts.md) | Rows for Shipping and Notifications that reach nothing but the broker |
| [§9.1](../09-messaging.md) | That Shipping "should not call back" for an address |
| [§9.7](../09-messaging.md) | The pricing hop as the platform's one synchronous call between its services |
| [§11.5](../11-identity-authorization.md) | One host in the table of realm objects, and "the platform's only permitted synchronous hop" |
| [§12](../12-test-strategy.md) | One suite that runs a real Keycloak, for one client |
| [§14.1](../14-local-development.md) | One client secret among the Compose defaults |
| [§15.2](../15-cicd-deployment.md) and [§15.4](../15-cicd-deployment.md) | "One client secret in the whole platform", and an inventory row marked BFF only |
| `docs/secrets.md` | The BFF as "the only host that calls a peer synchronously" |

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
