# ADR-174 — The saga outbox runs two transactions a message (#1603); supersedes ADR-032's default delivery limit

**Decision.** [§9.6](../09-messaging.md)'s saga endpoint keeps
[ADR-032](ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)'s
outbox, its tables and its pipe, and replaces the one class that decides where
each transaction begins and ends: `SagaOutboxContextFactory`, registered in
place of the `EntityFrameworkOutboxContextFactory<OrderingDbContext>` that
`AddEntityFrameworkOutbox` adds, runs a message in two. The first inserts the
`InboxState` row and consumes the message; the second delivers what the
transition staged, marks the row delivered and removes the staged rows. A pass
no longer saves its `LockId` and `ReceiveCount` in an `UPDATE` of their own;
they ride with the pass's own write. The endpoint's `MessageDeliveryLimit`
is `SagaOutboxDeliveryLimit`, ten, above the three a transition stages at
most, where MassTransit's default is one.
**Why.** #1603 read Query Store over a stress run and found the library's
`UPDATE` of `ordering.InboxState` run 543 718 times against 78 014 inserts,
the database's largest cost at the knee and mostly page-latch waits, and the
log flush of the endpoint's many small commits outside those figures.
MassTransit 8.5.12's factory commits a new row before consuming it, saves
the two lock columns in an `UPDATE` at the top of every pass, and ends a
pass after one stage of its pipe: consume; deliver up to the limit, marking
the row delivered once the rest fit under it; remove. At a limit of one the
mark always falls to a pass of its own, so a transition staging *k* messages
took *k* + 4
transactions and 2*k* + 5 `UPDATE`s, seven for one message, which is the
ratio Query Store read. Here it takes two transactions, an insert and two
`UPDATE`s, for any *k* below the limit. The consume still commits before
the broker sees a message, which is the outbox's guarantee, and a delivery
now commits with its mark and its removal, so nothing is staged twice or
lost.
**Consequences.** A class the library owns is now ours to keep in step with
it: a bump that changes the pipe's stages, or what the factory hands it, has
to be read against this one, and `OrderFulfilmentSagaEndpointTests` reads a
delivered row's `ReceiveCount`, one here and three under the library's
factory, so a pass count that moves fails there. `ReceiveCount` still counts
passes less one, so ADR-032's warning that it is no redelivery count stands.
A second delivery of a new message now waits on the first's key lock for the
whole consume rather than for an insert: one that read before the first's
insert fails its own into the endpoint's retry once the first commits, and
one that reads after it waits at its read and finds the row consumed. The
row lock serialises the two from there, so its messages are delivered once.
The delivering
transaction holds the row across every staged send, up to three where it
held one, each bounded by MassTransit's `MessageDeliveryTimeout`, so
ADR-032's degraded-broker consequence scales with the transition. The effect
on the stack's knee is not measured here; the stress run of
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)
is how it would be.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
