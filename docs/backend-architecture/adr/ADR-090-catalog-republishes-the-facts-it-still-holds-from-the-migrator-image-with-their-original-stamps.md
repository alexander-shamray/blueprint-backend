# ADR-090 — Catalog republishes the facts it still holds, from the migrator image, with their original stamps

**Decision.** Catalog's migrator image takes `Republish:Enabled=true` and then
stages the catalogue again on the Broker lane instead of migrating: for every
product a `ProductPublished`, stamped `PublishedAt` and carrying the name,
thumbnail and price the product has now; a `PriceChanged` stamped
`LastEventAt` where a product not withdrawn has been repriced since it was
published; and a `ProductDiscontinued` stamped `WithdrawnAt` where it is
withdrawn. `Republish:ProductId` limits the run to one product, and an id that
does not parse refuses the host rather than widening the run. Every row gets a
new `MessageId` and, as its own `OccurredAt`, the time of the run; the
payload's stamp is the event's own. The run reads and writes through the
runtime connection string, `ConnectionStrings:Catalog`, and the host refuses
to build without it. The three statements commit together, the run needs no
environment gate, and it exits non-zero when it fails or when the named
product does not exist. [§6.6](../06-cqrs.md)'s rebuild procedure for
`ordering.ProductPrices` and `ordering.Products` is this run.

**Why.** Ordering holds no source of truth for a price or a name, so a lost
row, a restore or a product published before `ordering-catalog-events` was
bound can only be repaired from Catalog, and until it is every order that
names the product fails with a 422 and no error in any log. A stamp of `now`
would defeat the guards the projection relies on: its withdrawal watermark
compares against the event's own `OccurredAt`, so a fresh one re-lists every
product ever discontinued. Catalog keeps exactly the stamps the three events
need, which is what [ADR-075](ADR-075-a-products-events-are-stamped-in-the-order-its-writes-committed.md)
added `LastEventAt` for. A fresh `MessageId` is what lets a second run
deliver, since the inbox drops a repeated one
([§9.5](../09-messaging.md)); the projection's strict `UpdatedAt`
comparison is what makes the redelivery of a fact it already has a no-op.
The row's own `OccurredAt` is the run's, because the outbox ages and claims
by it ([§9.4](../09-messaging.md)) and a catalogue's worth of rows aged from
last year's stamps would page `OutboxBrokerLaneStalled` for a backlog that is
minutes old. The migrator image carries it because it is the
one deliverable that is a job, with Catalog's context and the SQL-only shape
[§4.2](../04-solution-structure.md) already gives the seeder, and adding it
needs no HTTP surface, permission or realm client. An endpoint on Catalog's
API was rejected for those three. The run takes the runtime login and not the
migrator's, because the migrator holds `db_ddladmin` and `db_datawriter` and
no read role ([§7.1](../07-persistence.md)), while the runtime has DML on the
schema and stages these same rows in ordinary operation, so a data run holds
no DDL rights.

**Consequences.** The replay is the current state of each product with the
stamps it was given, not its history: a price held between the first and the
last change is not kept anywhere, and a withdrawn product gets no
`PriceChanged`, only the publication carrying the price it held at withdrawal.
Ordering ends in the state a product that had never lost a row would
be in, which is all a rebuild needs. This is the one run of a migrator image
that reads the runtime key, which [§7.1](../07-persistence.md)'s name boundary
otherwise reserves for hosts, so its Job is given the runtime Secret and never
the migrator's. Nothing in the chart runs it: the pre-upgrade hook stays a
migration, and a cluster operator starts a Job from the migrator image with
the flag set. Each run stages the whole catalogue again whether or not
Ordering needed it, so a catalogue of any size costs outbox volume that the
retention purge ([§9.4](../09-messaging.md)) reclaims, and a repeated run
repeats it. `messaging.delivery.lag` measures from the payload's stamp
([§13.3](../13-observability.md)), so the consumers record lags as old as the
products while the backlog drains, and `DeliveryLagHigh` can fire on Ordering
during a republish: that is the republish, not a fault. A product published or repriced while
the run reads it is announced by its own commit as well, and the later stamp
wins. The run does not touch Ordering, so it cannot say which rows were
missing, and it announces nothing for a product Catalog itself has lost.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
