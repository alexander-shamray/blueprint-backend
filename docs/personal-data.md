# Personal data — where it lives

**Every place this platform puts personal data, who owns it, how long it is
kept and how it is erased.** It is the operational half of
[§11.7](backend-architecture/11-identity-authorization.md) on the terms
[`docs/secrets.md`](secrets.md) holds for §15.4: the rule — integration events
carry identifiers, a reader keeps what it reads in its own table, erasure is a
choreography — is the chapter's and the ADRs', and this file is the list the
rule produces. Where the two disagree, §11.7 and the ADRs it cites win.

**This is the raw material of a record of processing, and not one.** It names
no purpose, no lawful basis, no category of data subject and no transfer
safeguard, because those are the adopter's
([ADR-062](backend-architecture/adr/ADR-062-the-domain-is-a-reference-implementation.md));
it says where the data is, which is the half an engineer can answer from the
code.

**It cites owners and restates none of them.** A window is named by the option
that sets it and never by its number, and a deployment value by the chart key
that carries it.

## Reading the columns

- **Holds** — what the holding keeps that is about a person. A customer's id is
  listed: it is pseudonymous, not anonymous, and
  [ADR-035](backend-architecture/adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
  calls it personal data.
- **Kept for** — the option that bounds it, or what ends it where no option
  does.
- **Erasure** — *delete*, *anonymise* or *lifetime only*, as §11.7 sorts them.
  §11.7's erasure is an extension, and its consumers are owed with it rather
  than built, so every *delete* and *anonymise* below is the path the owner
  has decided and not one that runs today.
- **Runs in** — the store, or for a third party the chart value naming it
  ([ADR-053](backend-architecture/adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)'s
  rule 3).

## The services' own stores

| Holding | Owner | Holds | Kept for | Erasure | Runs in |
|---|---|---|---|---|---|
| `ordering.Orders` | Ordering | The customer's id and the shipping address | No window: the order is the financial record | Anonymise: the id replaced, the address cleared (§11.7) | Ordering's database |
| `ordering.OrderSummaries` | Ordering | The customer's id, as the key of the buyer's list | No window, as its order | None decided: §11.7 draws Ordering's step over `Orders` alone | Ordering's database |
| `ordering.OrderFulfilmentStates` | Ordering | The customer's id on the saga's instance | Until the saga finalises, which deletes the row ([§9.6](backend-architecture/09-messaging.md)) | Lifetime only | Ordering's database |
| `ordering.OutboxMessages` | Ordering | The customer's id in the payloads of the order events that carry one | `RetentionPolicy.OutboxWindow` after dispatch; an abandoned row until an operator acts on it ([§9.4](backend-architecture/09-messaging.md)) | Lifetime only | Ordering's database |
| `payments.PaymentOrders` | Payments | The customer's id, read from `OrderPlaced` as the payer ([ADR-028](backend-architecture/adr/ADR-028-a-money-movement-command-carries-no-subject.md)) | No window | None decided: §11.7 draws no step for Payments | Payments' database |
| `shipping.DeliveryAddresses` | Shipping | The customer's id and the postal address read from Ordering ([ADR-052](backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)) | `ShippingJurisdictionOptions.AddressRetention` after its shipment is terminal | Delete (ADR-052) | Shipping's database |
| `shipping.TrackingEvents` | Shipping | The carrier's events for a parcel, personal only through the parcel's order | `ShippingJurisdictionOptions.TrackingRetention` after delivery | Lifetime only | Shipping's database |
| `notifications.ContactRecords` | Notifications | The customer's id, mailbox and language, read from Keycloak (ADR-052) | `NotificationsJurisdictionOptions.ContactRetention` since it was last read; served for no longer than `ContactOptions.StaleCeiling` | Delete (ADR-052) | Notifications' database |
| `notifications.NotificationLog` | Notifications | The customer's id and the message's parameters — the order, an amount, a tracking number, a cancellation reason — and never the mailbox or the body (ADR-053 rule 4) | `NotificationsJurisdictionOptions.LogRetention` after the row ends; a `Pending` row until `DeliveryOptions.GiveUpAge` | Anonymise the ended row (ADR-053 rule 4); delete the waiting one (ADR-052) | Notifications' database |
| `notifications.OrderRecords` | Notifications | The customer's id per order | `NotificationsJurisdictionOptions.OrderRetention` once no pending notice names its order | None decided: §11.7 draws no step for it | Notifications' database |
| `bff.Orders` | The BFF | The customer's id, as the buyer an order is shown to, and a tracking number ([ADR-051](backend-architecture/adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)) | No window | None decided: §11.7 draws no step for the BFF, and ADR-051's rebuild re-reads what the publishers' outboxes still hold | The BFF's database |

**Checked and holding none**: Catalog's and Inventory's tables, every
service's inbox — §9.5's `InboxMessage` keeps no payload, under
`RetentionPolicy.InboxWindow` — the idempotency markers and claims, which key
a command by its id under `RetentionPolicy.IdempotencyWindow`, and Redis, whose
coordination keys hold claims and locks and whose cache no read fills
([§8.2](backend-architecture/08-caching-redis.md)).
Shipping's `Shipments` holds an order id and a tracking number, which identify
a parcel and nobody until joined to the order.

## Stores the platform writes to and does not run

| Holding | Owner | Holds | Kept for | Erasure | Runs in |
|---|---|---|---|---|---|
| The broker's queues | Each consuming service | The customer's id in the order events not yet consumed, and in any parked in an `_error` or `_skipped` queue | Until consumed; a parked message until an operator acts on it. No chapter bounds it (§11.7) | Lifetime only | The broker the deployment runs ([§15.3](backend-architecture/15-cicd-deployment.md)) |
| The log and trace stores | The deployment | The customer's id and order ids, in log attributes and span attributes ([§13.4](backend-architecture/13-observability.md)) | The lifetime §13.4 has the deployment state | Lifetime only (§11.7) | The stores the deployment runs |
| Keycloak | The realm's operator | Each customer's name, mailbox, credentials and `locale` attribute — the owner of the contact Notifications reads | The realm's | Deleting or disabling the user, which is the deployment's act: Keycloak is no participant in §11.7's choreography (ADR-052) | The realm the deployment runs (§11) |

## Third parties shown personal data

Each is a processor with a country, named by a chart value and never a
constant (ADR-053 rule 3); what it keeps and for how long is its agreement
with the adopter, not this platform's.

| Processor | Shown | Named by |
|---|---|---|
| The carrier | A shipment's id and its postal address, with no name — `BookingRequest` | `carrier.baseUrl`, `deploy/helm/shipping/values.yaml` |
| The mail relay | The mailbox and the rendered message | `mail.host`, `deploy/helm/notifications/values.yaml` |
| The payment provider | The order's id, the payer's id — the customer's — an amount and a currency, and never card data (ADR-053) — `AuthorisationRequest` | `paymentProvider.baseUrl`, `deploy/helm/payments/values.yaml` |

## Keeping it true

A pull request that adds a table, a column, a contract field, a log attribute
or an outbound call that carries a person's data adds its row here, and one
that adds a retention option names it here. Nothing checks either yet; the
reason is in the commit that wrote this file.
