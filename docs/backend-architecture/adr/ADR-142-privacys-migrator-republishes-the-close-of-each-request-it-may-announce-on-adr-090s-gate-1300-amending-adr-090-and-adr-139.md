# ADR-142 — Privacy's migrator republishes the close of each request it may announce, on ADR-090's gate (#1300), amending ADR-090 and ADR-139

**Decision.** Privacy's migrator image takes `Republish:Enabled=true` as
[ADR-090](ADR-090-catalog-republishes-the-facts-it-still-holds-from-the-migrator-image-with-their-original-stamps.md)
has Catalog's take it, and then stages on the Broker lane, instead of
migrating, a `PersonalDataDeleteClosed` for every closed request whose close
[ADR-139](ADR-139-an-erasure-check-asks-no-request-that-closed-before-its-data-left-the-owner-882-amending-adr-092-adr-097-adr-099-adr-135-adr-136-and-adr-138.md)
lets Privacy publish: one where no amendment removed a holder that Privacy
never heard from. The payload carries the request's id and its `ClosedAt` as
`OccurredAt`; the row gets a new `MessageId` and the run's time as its own
`OccurredAt`. `Republish:Id` limits the run to one request, and the run exits
non-zero when that request does not exist, has not closed or may not be
announced, and when it fails. It reads and writes through
`ConnectionStrings:Privacy`, as ADR-090's run does. Once the release that
publishes the close is running, an operator runs it once, with no id, from a
Job started from the migrator image with the flag set.
**Why.** ADR-139 left every request closed before that release with its audit
rows open at every holder, so each check still tries those rows' hashes, the
cost #882 measured, for ever. Privacy keeps the close time and the amendments
that decide whether a close may be told, and the holders keep nothing that
could rebuild either, so the announcement can only come from Privacy. The
test is the one `ErasureRequest` applies when it closes, read from the rows it
left: a closed request records no later answer, so the completions on the row
are the ones the close saw. ADR-090's job already carries the gate, the
runtime key and the SQL-only shape [§4.2](../04-solution-structure.md) asks of
a migrator, and adds no endpoint, permission or realm client; a migration step
was rejected because a migration runs on every upgrade with the migrator's
login, which holds no read role ([§7.1](../07-persistence.md)).
**Consequences.** ADR-090's run is no longer the only one of a migrator image
that reads the runtime key: Privacy's Job is given the runtime Secret too, and
never the migrator's. Each run stages every announceable close again, already
told or not; a holder keeps the first close it stamps and stamps nothing for a
request it holds no row for, so a repeat costs outbox volume and a delivery
per holder and changes no audit row. Each holder records a delivery lag as old
as the close it is told ([§13.3](../13-observability.md)), and
`DeliveryLagHigh` can fire while the backlog drains; that is the republish,
not a fault. A close over a removed
holder never heard from is still never told, so its rows stay open, as ADR-139
says. Nothing in the chart runs it, so until an operator does, every request
closed before the release keeps its rows open.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
