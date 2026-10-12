# Appendix E — The reference domain

This appendix owns every fact about the reference domain that a chapter no
longer states: the services and what each is, their contracts, queues, ports,
realms, Compose units and CI filters where no other file holds them, and the
worked examples. A chapter states a rule abstractly and cites a section here
by link, never a value; an adopter replaces this appendix wholesale
([ADR-096](adr/ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md),
[ADR-062](adr/ADR-062-the-domain-is-a-reference-implementation.md)).

Each section below is a skeleton, one per chapter pass, so that a pass fills
the section it cites, replacing its "Filled by" line, and creates none.

## E.1 §2 and §3: the service roster and the context map

Filled by #907.

## E.2 §4: the solution tree and the scaffold

Filled by #904.

## E.3 §5, §7 and §8: aggregates, persistence and caching

Filled by #908.

## E.4 §6: CQRS and its projections

Filled by #910.

## E.5 §9: messaging and the sagas

Filled by #909.

## E.6 §10 and §11: the gateway routes and the identity realms

Filled by #911.

## E.7 §12: the test strategy

Filled by #905.

## E.8 §13 and §14: observability and local development

Filled by #906.

## E.9 §15: CI/CD and deployment

[§15](15-cicd-deployment.md) states each rule abstractly, with `<service>` and
`<Service>` standing for a service's lower-case and directory spellings. This
section holds the instances those placeholders stand for. Where a file already
holds a roster it is cited and not copied: the change-detection filters and the
image matrix are `.github/workflows/ci.yml`'s `changes` and `images` jobs, a
service's Dockerfiles sit beside its projects under `src/Services/`, its chart
is `deploy/helm/<service>/`, and the Compose units are `deploy/compose/`.

### E.9.1 The change filter and the proto-reaching filters

§15.1's sample filter is Ordering's: the shared anchor,
`src/Services/Ordering/**` and `tests/Ordering.*/**`. Every other service
repeats those three lines under its own key, and `.github/workflows/ci.yml`
holds the entry of each.

The two filters that reach into another service's tree:

| Filter | Extra path | Why |
|---|---|---|
| `bff` | `src/Services/Catalog/Catalog.Api/Protos/**` | Web.Bff compiles Catalog's `pricing.proto` as a linked source file (§9.7) and its Dockerfile copies that path |
| `shipping` | `src/Services/Ordering/Ordering.Api/Protos/**` | Shipping's images copy Ordering's `Protos/` for the delivery-address contract, on the BFF's terms |

`tests/Ordering.*/**` covers `Ordering.TestSupport` as well as Ordering's three
test projects, so Ordering's test helpers do not belong to `shared`, and
touching Catalog's test data builders redeploys Catalog alone.

### E.9.2 The images

§15.2's sample Dockerfile is `src/Services/Ordering/Ordering.Api/Dockerfile`,
whose entry point is `Ordering.Api.dll`, and its migrator's is
`src/Services/Ordering/Ordering.Migrator/Dockerfile`, whose entry point is
`Ordering.Migrator.dll`. The `images` job's matrix lists the pair for Ordering
as `filter: ordering` with the images `ordering-api` and `ordering-migrator`;
every other service has its own pair there.

### E.9.3 The charts

§15.3's sample `values.yaml` is `deploy/helm/ordering/values.yaml`, the chart
of a service that has a database, a broker and Redis and calls no peer. Its
instances of the placeholders are:

| Value | Ordering's |
|---|---|
| `workload.name` | `ordering-api` |
| `image.api`, `image.migrator` | `ordering-api`, `ordering-migrator` |
| `database.connectionName` | `Ordering`, and `OrderingMigrator` in the hook |
| `database.runtimeSecretRef.name` | `ordering-database` |
| `database.migratorSecretRef.name` | `ordering-migrator-secret` |
| `broker.secretRef.name` | `ordering-rabbitmq` |
| `redis.secretRef.name` | `ordering-redis` |

Ordering calls no peer: Shipping reads its delivery address over the RPC
([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)),
its prices come from a local projection (§6.4) and the rest goes over the
broker, so its `identity.clientCredentials` is `false`.

The reference domain's workers are Shipping and Notifications. Each consumes
from the broker and exposes no API, and each chart, `deploy/helm/shipping/` and
`deploy/helm/notifications/`, writes `service.enabled: false` and
`ingress.enabled: false` down. `deploy/helm/shipping/values.yaml` is the
instance of §15.3's worker fence. Against Ordering, a worker differs by
`service.enabled` alone among those two keys. The Ingress is the gateway's
alone; every API host and the BFF are reached through the edge
(§10.1, §10.2).

Each worker's chart sets `autoscaling.enabled: false` and `replicaCount: 3`,
three for availability across a node drain. What says three is too few is
`shipping.shipments.overdue` for Shipping and `notifications.overdue` for
Notifications. The queue-backlog rule of §13.6 watches each service's receive
endpoint, and each one's consumers only write a row: a `Pending` shipment or a
cancellation for Shipping, a `Pending` notification or an order record for
Notifications. The replica-bound work is the workers': Shipping's fulfilment
and tracking passes, and Notifications' send worker. Shipping's gauge is the
wait of the rows a pass is due to claim and has not, by pass; Notifications'
is that wait for its one pass, and `notifications.waiting`, by the step a row
waits on, is the dependency's wait beside it rather than the pass's. For
Shipping and Notifications alike, the backlog procedure of
[`queue-backlog.md`](../runbooks/queue-backlog.md) is the cue
([ADR-063](adr/ADR-063-a-worker-that-waits-on-a-third-party-is-scaled-by-hand.md)),
and the ceiling is the carrier's or the relay's rate limit rather than the
queue's depth.

### E.9.4 The options a worker binds

`Shipping.Worker` and `Notifications.Worker` each register `CachingTokenClient`
and `ClientCredentialsHandler` in their own `Program.cs`, because each calls out
under a grant of its own. Without the binding, Web.Bff would request a token
with an empty scope and get 401s it would read as Catalog's fault.

The options types that earn a binding in the reference domain beyond
`Identity:Client`: `Mail` holds Notifications' relay and its credential,
`Jurisdiction` holds the statutory windows and, for Notifications, the
language set and the time zone, and `Fulfilment` (Shipping) and `Delivery`
(Notifications) each hold the give-up age of
[ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md).
The tempting counter-example is a `ServiceOptions`-shaped bag of batch sizes,
poll intervals and retry caps bound to an `Ordering` section that no
environment ever sets.

### E.9.5 The host-specific keys

§15.4's inventory holds the keys the gateway, the BFF and the workers read,
and those every host shares. These are the keys one service's code reads and
§15.4 does not list, in the same columns. A key joins a chart when its host's
code reads it, and the host refuses to start without a required one.

| Key | Kind | Source | Required |
|---|---|---|---|
| `PaymentProvider__BaseUrl` | Config | ConfigMap | ✓ — **Payments only**; the provider's address, and the host refuses to start without it |
| `PaymentProvider__ApiKey` | Secret | External Secrets | ✓ — **Payments only**; the provider's credential, and the host refuses to start without it |
| `Carrier__BaseUrl` | Config | Helm `carrier.baseUrl` → ConfigMap | ✓ — **Shipping only**; the carrier's address, and the host refuses to start without it |
| `Carrier__ApiKey` | Secret | Helm `carrier.apiKeySecretRef` → External Secrets | ✓ — **Shipping only**; the carrier's credential, and the host refuses to start without it |
| `AddressSource__BaseUrl` | Config | Helm `addressSource.baseUrl` → ConfigMap | ✓ — **Shipping only**; the owner's address for [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s read, and the host refuses to start without it |
| `Jurisdiction__AddressRetention` | Config | Helm `jurisdiction.addressRetention` → ConfigMap | ✓ — **Shipping only**; ADR-053's statutory window for a delivery address, and the host refuses to start without it |
| `Jurisdiction__TrackingRetention` | Config | Helm `jurisdiction.trackingRetention` → ConfigMap | ✓ — **Shipping only**; ADR-053's statutory window for a shipment's tracking events, and the host refuses to start without it |
| `Fulfilment__GiveUpAge` | Config | Helm `fulfilment.giveUpAge` → ConfigMap, defaulted in the chart | ✓ — **Shipping only**; [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s give-up age for a pending shipment, and [ADR-054](adr/ADR-054-a-shipment-stops-waiting-on-its-carrier-at-an-age.md)'s for an unanswered cancellation; the host refuses to start without it |
| `Privacy__Responders__0…n` | Config | Helm `privacy.responders` → ConfigMap | ✓ — **Privacy only**; [ADR-092](adr/ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)'s holders, read once when a request is raised and stored on it, and the host refuses to start without one, on a repeated name, or on one that is not a lower-case name of letters, digits and hyphens |
| `Privacy__CompletionSlo` | Config | Helm `privacy.completionSlo` → ConfigMap | ✓ — **Privacy only**; how long a request may stay open before it is overdue, the adopter's reading of the law (ADR-053), and the host refuses to start without it |
| `Mail__Host` | Config | Helm `mail.host` → ConfigMap | ✓ — **Notifications only**; the relay's host name, and the host refuses to start without it |
| `Mail__Port` | Config | Helm `mail.port` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; the relay's submission port |
| `Mail__From` | Config | Helm `mail.from` → ConfigMap | ✓ — **Notifications only**; the one sender every message carries, and the domain each `Message-ID` is minted under |
| `Mail__Security` | Config | Helm `mail.security` → ConfigMap, which admits `StartTls` alone | ✓ — **Notifications only**; `StartTls` or `None`, and `None` refuses to start outside Development |
| `Mail__UserName` | Config | Helm `mail.userName` → ConfigMap | ✓ **outside Development** — **Notifications only**; set with the password or not at all |
| `Mail__Password` | Secret | Helm `mail.passwordSecretRef` → External Secrets | ✓ **outside Development** — **Notifications only**; the relay's credential, and absent in Compose, where the sink takes unauthenticated submission |
| `ContactSource__BaseUrl` | Config | Helm `contactSource.baseUrl` → ConfigMap | ✓ — **Notifications only**; Keycloak's address for [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s contact read, HTTPS outside Development, and the host refuses to start without it |
| `ContactSource__Realm` | Config | Helm `contactSource.realm` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; the realm whose users are read, the one that issues this host's token, and the host refuses to start without it |
| `Jurisdiction__Languages__0…n` | Config | Helm `jurisdiction.languages` → ConfigMap | ✓ — **Notifications only**; ADR-053's language set, in the order a message in every language shows them; the host refuses to start unless every language has every template and a culture |
| `Jurisdiction__TimeZone` | Config | Helm `jurisdiction.timeZone` → ConfigMap | ✓ — **Notifications only**; the IANA zone a customer's dates are rendered in, resolved at start, so a zone the image does not know refuses the host |
| `Jurisdiction__LogRetention` | Config | Helm `jurisdiction.logRetention` → ConfigMap | ✓ — **Notifications only**; ADR-053 rule 4's statutory window for the record of a send, and the host refuses to start without it |
| `Jurisdiction__ContactRetention` | Config | Helm `jurisdiction.contactRetention` → ConfigMap | ✓ — **Notifications only**; ADR-052's contact row's window, refused at start when shorter than `ContactOptions.StaleCeiling` |
| `Jurisdiction__OrderRetention` | Config | Helm `jurisdiction.orderRetention` → ConfigMap | ✓ — **Notifications only**; the order record's window, and the host refuses to start without it |
| `Delivery__GiveUpAge` | Config | Helm `delivery.giveUpAge` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s give-up age for a notification still pending, past which it is undeliverable with the reason `NotificationReasons.GaveUp`; the host refuses to start without it, or with one longer than `RetentionPolicy.InboxWindow` |

---

[← Appendix D](appendix-d-type-inventory.md) · [Index](README.md)
