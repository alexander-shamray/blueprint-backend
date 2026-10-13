# ADR-115 — A device's push token is a contact Notifications owns, registered through the BFF and erased with its subject (#474)

**Decision.** A push token is **personal data and a contact** — an address a
message is sent to — and Notifications owns it, as the service that sends
every message to it. It is not a contact in
[ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s
sense, a copy of a value read from its owner: Notifications is the owner, no
worker reads it from anywhere, and the names that service already gives
ADR-052's mailbox row — its contact records, its contact retention, the
contacts Appendix E.6.9 says it erases — do not include it.

It is kept in a table of Notifications' own, never in Redis
([ADR-006](ADR-006-redis-for-cache-and-coordination-never-as-a-store-of-record.md)),
**one row per installation of the app**, keyed by an installation id, a random
UUID the app mints once per install. The row holds the subject, the platform
(`fcm` or `apns`), the app id, the token, the installation's last sequence,
when the row was registered and when it was last seen, and nothing else — no
device model, operating system version or locale, because routing a message
needs none of them. A token the provider rotates replaces the one before it in
the same row, and a sign-out names the row without putting a token in a URL.
**A device is one subject's at a time**: a registration by a second subject
moves the row to that subject, because the device now shows whoever signed in
last and must never show the one before, and the platform, app id and token
are unique across the table, so a token that reappears under a new
installation leaves the old one. A subject holds at most
`DeviceTokenOptions.MaxPerSubject` rows, ten, and a registration past that
drops the row seen least recently.

**The client registers through the BFF**: `PUT /bff/v1/devices/{installationId}`
with the platform, the app id and the token, sent on every start of the app
and on every rotation, and `DELETE` on the same path at sign-out, answered 204
whether or not a row was there. **Each request carries the installation's
sequence**, a counter the app raises before every registration and sign-out
and keeps with its installation id, and Notifications applies a request only
when its sequence is above the row's: one equal to it is a repeat and is
answered as the first was, and one below it is stale: it changes nothing and
is answered 409 with the row's sequence, so an app whose counter has fallen
behind its row — restored from a backup, or overtaken by the move below —
raises its counter past it and sends its present state again. A sign-out deletes the subject and the token and keeps only the
installation id and its sequence, until the stale window removes those too.
That makes both routes `Convergent`
([ADR-058](ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md))
inside that record's limit, because the interleaving it names cannot land: a
retried registration that arrives after the sign-out, after a rotation or
after another subject's registration carries a lower sequence than the write
it would undo. A `DELETE` acts only on a row whose subject is the caller's.

The BFF refuses a malformed request — an installation id that is not a UUID,
a platform outside the pair, an app id the deployment does not name, a token
past a bounded length or outside printable ASCII, a sequence that is not a
positive integer — and otherwise makes **one synchronous call** to
Notifications over gRPC ([§9.7](../09-messaging.md)), carrying the caller's
own access token unchanged, as the gateway forwards it
([§11.5](../11-identity-authorization.md)). The client carries the
correlation handler and no client-credentials handler. Notifications
authenticates that token and takes the subject from it, so no request names a
subject and no credential the BFF holds can register a device to somebody
else. The method is served on an HTTP/2-only port that no gateway route
matches, and **this record amends
[ADR-065](ADR-065-every-workload-is-fenced-by-a-default-deny-networkpolicy.md)'s
table**: the Notifications workload that serves the method admits `web-bff`
on `grpc`, and `web-bff` reaches that workload on `grpc`. Which workload that
is — the worker, whose chart has no Service today, or a host of its own — is
decided with the host the push adapters run in, because the two are one
question, and the table's row is named when it is.

**A row not seen for `DeviceTokenOptions.StaleAfter`, thirty days, is
deleted** by the service's retention pass, and start-up refuses a window of
zero, or one past the 270 days after which FCM expires an inactive token
itself. **A provider's answer that a token is unregistered** — FCM's
`UNREGISTERED`, APNs' `410` — deletes the row in the transaction that records
the notice's outcome, **and only while the row still holds the token the send
used**, so a rotation that landed during the send keeps its new token; the
answer is a terminal outcome and never an exception or a retry, and the
vocabulary that carries it is the push port's. **`PersonalDataDeleteRequested`
clears every row of the subject** as a sign-out does — the subject and the
token deleted, the installation id and its sequence kept for the stale
window, linked to nobody — so a retry of a registration applied before the
erasure changes nothing, and one that never landed before it is a
registration after the erasure, below. It runs in the transaction
Notifications' erasure handler already runs, so the responder set fixed by
[ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)
does not change. A token is never a log attribute, a span attribute, a metric
label or part of an exception's message ([§13.4](../13-observability.md)).
**Why.** The token is personal data on every reading this platform answers
to: GDPR's Recital 30 names the online identifiers a device provides, the
CCPA's unique identifier includes a device's, and both platforms' own
policies treat the token as data linked to an account. It cannot travel by
event, and not because it is personal data, since `CustomerId` is that and
travels: [ADR-035](ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
lets an identifier onto the bus because its resolution lives in one store the
platform can sever, and a token resolves at FCM or APNs, outside any store the
platform owns, to a device a message reaches directly. So its owner is the
question. The sender owns it because the sender is the one that reads it on
every send and the only party a provider tells that it is dead. Kept by the
BFF, as
[ADR-107](ADR-107-the-bff-keeps-a-customers-saved-delivery-address-and-an-erasure-deletes-it-473.md)
keeps the saved address, every send would read it across a hop by ADR-052's
worker read, with a freshness and a stale ceiling for a value that rotates,
and every revocation would be a write back to the owner from the send path;
owned by Notifications, both are a row in one transaction. A Keycloak
attribute would put a value that changes with every install into the
identity provider, and into the user representation a grant that reads every
user already reaches.

The registration is a synchronous call because a person is waiting on its
answer, which is [§2.3](../02-architecture-at-a-glance.md)'s condition, and
it is the request's one hop, which is
[ADR-017](ADR-017-one-synchronous-hop.md)'s. It carries the caller's token and
not a credential of the BFF's own because of what each is worth stolen: a BFF
client holding a permission that reaches across subjects — the shape ADR-052
gave Shipping's read, which skips the ownership check — would let a stolen
secret attach an attacker's device to any buyer and show that buyer's orders
on the attacker's lock screen, where a buyer's token registers that buyer and
nobody else. §11.5's warning against reusing a user's token is about
background work, and this is the user's own request, made while they wait.

The row is keyed by the installation and not by the token, as the issue first
put it, because a rotated token keyed on itself leaves the old row sending
until the window or the provider ends it, and a sign-out by token has to put
the token in a path or in a body a `DELETE` is not expected to carry. The
sequence is the installation's and not a clock's, because the app is the one
party that knows the order of its own acts, and a key per request would not
do: [§8.5](../08-caching-redis.md)'s key stops a repeat of one request, not a
stale one landing after a different request. Thirty days is FCM's own measure
of a stale token, and an app that registers on every start keeps the row of
any buyer who opens it once a month; a longer window keeps rows a provider
will refuse. The cap bounds a client that registers in a loop.
**Consequences.** **Notifications becomes a callee.** It serves a port, and
while the host serving it is down a registration fails; the app retries on its
next start, and sends are unaffected. That amends
[ADR-104](ADR-104-an-operator-may-switch-off-notifications-shipping-and-privacy-for-a-bound-and-no-other-service.md),
which lets an operator switch Notifications off because it has no caller: a
suspension that takes the serving host down now also fails registrations,
while one that stops consumers and workers alone, as
[ADR-113](ADR-113-an-operator-switches-a-receive-endpoint-or-a-worker-off-from-a-file-that-reloads-and-no-feature-flag-library-is-adopted-954.md)'s
switch does, leaves the method serving. The call holds no client
secret, so the equation §11.5 owns, that the number of hosts holding one is
the number of synchronous couplings, stops being true — there is now a
coupling no secret counts, and a reader auditing couplings by the realm's
clients misses it. That amends the same equation where
[ADR-091](ADR-091-a-host-holding-client-credentials-makes-a-call-under-a-grant-of-its-own-and-not-always-to-a-peer.md)'s
and ADR-052's consequences state it.

**A buyer who does not open the app for thirty days stops receiving pushes**
until the next start, with no signal to the platform; mail is unaffected. **A
shared device** silently stops pushing to the subject who used it before, who
has signed out of it or been signed over. **An installation id is a bearer
name for its row**: a subject who learns another's can move that row to
themselves, which stops the other's pushes until that app's next start, when
the 409 sends it past the mover's sequence, and reads none of them; the id is
a random UUID the app never shows. **A registration made after an erasure**
makes a new row of the subject's own act, as ADR-107 accepts for the saved
address, and it comes back on every start of the app for as long as the
deployment leaves the subject able to sign in, since Keycloak takes no part in
the erasure; a later erasure request removes it.
[ADR-097](ADR-097-a-holder-that-writes-from-a-feed-asks-its-own-erasure-rows-after-it-writes.md)'s
check does not apply because no feed writes the table.

**This record decides the contact and not the channel**: the push port, its
result vocabulary, the two provider adapters, which events push and the
consent they are sent under are decided by the records and the specification
that follow it on #474, and nothing here is built until they are. **The pull
request that builds it amends what says otherwise**, as ADR-052 left its list
to the pull request that built it; the list is written out for that record's
reason:

| Owner | What it says today |
|---|---|
| [§2.2](../02-architecture-at-a-glance.md) | The BFF's one edge to a service, drawn as gRPC and a read |
| [§3](../03-bounded-contexts.md) | Notifications reached only through the broker, and the only pure consumer among the services |
| [§9.7](../09-messaging.md) | The pricing hop as the platform's one synchronous call on a request path |
| [§10](../10-api-gateway.md) | §10.1's one `Web.Bff` making one call to one service, §10.4's hop to the one service it calls, and no wire contract for the route beside §10.9's |
| [§11.5](../11-identity-authorization.md) | The equation above, and the credentials handler attached to every outbound client that calls a peer |
| [§15.4](../15-cicd-deployment.md) | One set of credentials per host that calls synchronously, as the number of couplings |
| [Appendix E](../appendix-e-reference-domain.md) | E.2.1's pure-consumer shape for Notifications and the scaffold's `--pure-consumer` rule, E.5.4's hops without this one, E.6.1's one synchronous call from the BFF, E.6.9's Notifications row without the tokens, and E.9.3's Notifications as a worker that exposes no API, whose chart writes `service.enabled: false` |
| [§15.3](../15-cicd-deployment.md), `deploy/helm/notifications/values.yaml` and `deploy/helm/smoke.sh`, where the worker is the host | A worker's chart with no Service and the health endpoint as its only listener, and a Service-less chart's policy asserted to admit nothing — **asserted**: the gate goes red |
| `docs/runbooks/latency.md` | One synchronous hop on a request path, BFF to Catalog, as the only one that can make a request wait |
| `deploy/helm/catalog/Chart.yaml` and `docs/repo-map.md` | The platform's two gRPC servers |
| `docs/personal-data.md` | No row for the table |

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
