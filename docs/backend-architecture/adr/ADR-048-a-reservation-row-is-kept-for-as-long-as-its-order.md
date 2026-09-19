# ADR-048 — A reservation row is kept for as long as its order

**Decision.** Inventory **never deletes** a `Reservations` row, in any
`ReservationStatus`, or its `ReservationLines`, and no purge reaps them. A row
still changes state as the order moves; what is kept is the row itself. The
platform states no lifetime for an order, so this table states none either. A
purge arrives only with a rule that bounds an order's life, owned by Ordering
and argued in the ADR that supersedes this one.

**Why.** Every row is an answer the order may ask for again, not only
[ADR-024](ADR-024-a-release-answers-for-the-order-not-for-the-reservation.md)'s
tombstone. `Reservation.AnswerAgain` repeats a `ReserveStock`'s answer from the
row it finds — `StockReserved`, `StockReservationFailed` with the ids it named,
or `StockReleased` — and a release after despatch answers from the `Fulfilled`
row without returning stock that has left ([§3.2](../03-bounded-contexts.md)).
With the row gone, the same message for the same order takes a fresh reservation
for an order that has already been settled, which is the stranding ADR-024
exists to prevent. So the row's life is the life of every message that can still
name its order, and ADR-024 already records that such a bound is the order's
lifetime, not a figure off a retry ladder or a despatch timeout; its "reaped
afterwards" stands, and this ADR says only that no afterwards is stated yet.
Nothing in the platform deletes an order: the saga instance is finalised and
removed, the `Orders` row is not. A bound written here would be the one number
nothing in the system can derive, and reaping early turns housekeeping into a
correctness defect, where keeping the row costs storage.

**Consequences.** The table grows by one row for every order that reaches
Inventory, for ever, and by a line per product for each of those that asked for
stock; ADR-024's tombstone carries none. That is the same growth Ordering's
`Orders` and `OrderLines` already carry with no bound of their own, so Inventory
adds a constant factor to a cost the platform has taken rather than a new one —
and it is the cost a future reader will resent when the table is the largest in
the database. It is not free in the other direction either: a retention rule for
orders, when one is decided, now has two stores to reach rather than one, and
the ADR that states it owes Inventory a purge that deletes no row a live order
can still ask about.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
