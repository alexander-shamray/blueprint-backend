# ADR-075 — A product's events are stamped in the order its writes committed

**Decision.** `Product` keeps `LastEventAt`, the stamp of the newest event it
raised, and stamps each later one at the time it is given or, where that is
no later, one tick after `LastEventAt`. `ProductPublished` sets it,
`PriceChanged` and `ProductDiscontinued` advance it, and `WithdrawnAt` is the
withdrawal's stamp. The row version already serialises a product's commits
([§7.3](../07-persistence.md)), so a product's stamps follow the order
its writes committed in, whichever replica's clock each read. The migration
gives an existing row its withdrawal or else its publication, because a
price change left no column behind. The column defaults to the database's
time, because the release still running beside this one inserts without it
([§7.4](../07-persistence.md)).

**Why.** Ordering's price projection orders a product's events by
`OccurredAt` alone ([§6.6](../06-cqrs.md)). Stamped from each replica's own
clock, a price committed on a replica running ahead could carry a later
stamp than a withdrawal committed after it, and the withdrawal's watermark
then did not cover the price row: a withdrawn product stayed orderable, the
residual ADR-074's *Why* names. The same skew inverted two prices, so the
older amount won. Stamping in commit order closes both at the publisher,
which is the only place that knows the order. The per-product sequence §6.6
proposed instead was a fourth field in §9.1's envelope for every service, a
versioning decision under §9.2 and a counter of its own; the stamp is one
column on one table and a contract that does not move. Persisting only the
last price's stamp for `Withdraw` to clear was rejected, because it closes
the withdrawal pair and leaves the two-price inversion open.

**Consequences.** A product's stamp can run ahead of the wall clock by the
skew between two replicas, plus a tick for each event the product raises
before the slower clock catches up: it moves a tick past the last one only
where the clock has not. Two prices for one product no longer share a
stamp, so a tie the projection's strict comparison refuses is only a
redelivery, which ties with itself. The order holds once every replica runs this
release. Until then, during a canary
([ADR-022](ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md))
or after a rollback, the previous release changes prices without advancing
`LastEventAt`, so a price or a withdrawal within one skew of such a change
can still be stamped before it: the residual this record closes, narrowed to
that window and named rather than guarded. The seeder writes `LastEventAt`
beside `PublishedAt`, since it inserts rows in SQL rather than through
`Product.Publish`. An
operator who sets a column in SQL, as ADR-074's consequences allow for a
seller, leaves `LastEventAt` alone, and nothing is wrong with that. Events
from Ordering's own lifecycle and from every other service still carry their
own clock's stamp; this record is about Catalog's three product events.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
