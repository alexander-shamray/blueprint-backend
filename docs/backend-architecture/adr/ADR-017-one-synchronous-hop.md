# ADR-017 — One synchronous hop

**Decision.** At most one synchronous downstream service call per inbound
request. Synchronous calls inside message consumers require a written exception.
**Why.** Availability multiplies and latency accumulates down a chain. Four
services at 99.9% give 99.6% — 43 minutes of monthly downtime becomes nearly
three hours with no service having missed its own target.
**Consequences.** Cross-context data must arrive by event and be projected
locally, which means designing for staleness in the UI. That is the intended
trade.

> **Two values are fetched rather than delivered, and nothing here has been
> edited.** [ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
> reads a delivery address and a mailbox from their owners, because
> [ADR-035](ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
> keeps both off the bus. The read is made by a worker over a row and never
> inside a consumer, so the exception this record asks for is not spent.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
