# ADR-112 — The saga's outbox transaction is ReadCommitted and only the saga's row lock is serializable (#992); supersedes ADR-032's Serializable outbox transaction

**Decision.** `AddEntityFrameworkOutbox<OrderingDbContext>` sets
`IsolationLevel = IsolationLevel.ReadCommitted`, and §9.6's
`EntityFrameworkRepository` takes `LockStatementProvider =
new SqlServerLockStatementProvider(serializable: true)`, held as
`Ordering.Infrastructure.Messaging.DependencyInjection.SagaLockStatement`. The
saga's pessimistic load is then `SELECT * FROM [ordering].OrderFulfilmentStates
WITH (UPDLOCK, ROWLOCK, SERIALIZABLE) WHERE CorrelationId = @p0` inside a
`ReadCommitted` transaction: the hint takes the key-range lock on the one
statement that needs it, and nothing else in the transaction is serializable.
`MessagingRegistrationTests` pins both values without a container, and
`OrderFulfilmentSagaEndpointTests` reads the statements the host really sends,
with the isolation level of the transaction each ran in.
**Why.** [ADR-032](ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)
needed a key-range lock for one read: two deliveries both taking the
`Initially` branch for a `CorrelationId` with no row yet. It took it by making
the whole consume transaction `Serializable`, because the outbox opens that
transaction and the repository joins it. That level reaches MassTransit's own
reads too, and one is the outbox's delivery read, `SELECT TOP(@p) … FROM
[ordering].[OutboxMessage] WHERE InboxMessageId = … AND InboxConsumerId = … AND
SequenceNumber > @last`. Compiled against an empty table, SQL Server can choose
a clustered seek on `SequenceNumber > @last`, and under `Serializable` that open
range takes `RangeS-S` up to the last key, where every concurrent saga insert
needs `RangeI-N`. [#976](https://github.com/alexander-shamray/blueprint-backend/issues/976)'s
diagnosis found that pair in every `system_health` deadlock graph of a
cold-start run, and evicting the plan stopped the deadlocks at that moment. The
victims retried on §9.8's policy, place-to-cancel went from 720 ms to 9.8–23 s,
and the burst released afterwards was 93% of the run's read-model staleness
tail. Nothing was lost; every order was late. Three other remedies were weighed
and refused. Pinning the plan, by a plan guide or a rewriting interceptor,
binds this service to the text of a query MassTransit owns. Re-clustering
`OutboxMessage` on `(InboxMessageId, InboxConsumerId, SequenceNumber)` makes the
bounded seek the natural plan, but under `Serializable` it still locks to the
next key, and on a drain table that is nearly always empty the next key is the
end of the table, where every insert lands. `RepeatableRead`, MassTransit's
default, takes no range locks but holds the delivery read's shared row locks to
commit; the clustered plan reads other messages' rows, each delivery's own
delete needs those rows' exclusive locks, and two deliveries doing both
deadlock. `ReadCommitted` releases each shared lock as the read moves on, so the
read holds nothing a writer waits for. The hint is MassTransit's own:
`SqlServerLockStatementFormatter` appends `, SERIALIZABLE` to its `UPDLOCK,
ROWLOCK` when constructed with `serializable: true`.
**Consequences.**

- **A cold plan can still make a delivery read wait, but no longer deadlock on
  it.** Without read-committed snapshot, which the Ordering database does not
  enable, the clustered plan still reads other consumers' uncommitted rows and
  waits for their commits. The writers it waits on are saga transactions and
  deliveries already past their own read, and neither waits on anything a
  reading delivery holds, so the wait is a queue rather than a cycle. That is
  argued from the lock modes and checked only by
  [#992](https://github.com/alexander-shamray/blueprint-backend/issues/992)'s
  cold-start rerun; a deadlock graph naming `OutboxMessage` after this
  decision is the evidence that the argument was wrong.
- **Read-committed snapshot was not taken.** It would remove even the wait,
  but it is a database-wide switch over every Ordering read, §9.4's dispatcher
  among them, each argued under locking reads. Changing that is a decision
  about the whole service, not about this endpoint.
- **The repository's own `IsolationLevel` is still not in force.** It is
  `Serializable` by default and applies only to a transaction the repository
  opens for itself; on this endpoint the outbox opens it first, as it has since
  ADR-032. Raising it again would change nothing, which is why the level is
  pinned on the outbox and the lock on the statement.
- **MassTransit's inbox row is still locked; only its absence is not.**
  `InboxState` is read with `UPDLOCK, ROWLOCK`, which holds to commit at any
  level, so two deliveries of one message still serialise once its row exists.
  Two *first* deliveries now race on `AK_InboxState_MessageId_ConsumerId`
  instead of a range lock: the loser fails on the duplicate key and §9.8
  retries it. That is MassTransit's behaviour at its own default level, and
  §9.5's `InboxFilter` stands in front of it either way.
- **The serializable part of the transaction is now one statement, and a
  second saga cannot inherit it by sharing the outbox.** A saga added to this
  outbox takes its own `LockStatementProvider`, or its first deliveries race
  as the Why above describes. ADR-032 says a second saga service inherits that
  decision by citing it; it now inherits two settings, not one.
- **ADR-032's §12.4 consequence narrows but does not close.** Respawn's reset
  still deletes under a saga transaction that may be committing, and lock
  order can still oppose it, so `ServiceFixture.ResetAsync` keeps retrying
  error 1205. The transaction it meets is serializable on the saga's row only,
  no longer across three tables.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
