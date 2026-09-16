# ADR-021 — Saga timeouts are scheduled by the broker

**Decision.** [§9.6](../09-messaging.md)'s saga schedules are delivered by
MassTransit's **delayed message scheduler** —
`AddDelayedMessageScheduler()` in the registration and
`UseDelayedMessageScheduler()` on the transport, both halves named because
either alone leaves `.Schedule(…)` throwing. On RabbitMQ that scheduler is the
`rabbitmq_delayed_message_exchange` plugin, so `deploy/compose/rabbitmq`
**builds** the broker image rather than pulling a stock one — the single
infrastructure service in [§14.1](../14-local-development.md) that is built. No
Quartz, no Hangfire, and no scheduler process of this platform's own.

**Why.** No chapter specified one, and the omission was invisible until a state
machine with four waits was compiled: `Initially` arms `StockTimeout`, so the
very first `OrderPlaced` reaches for a scheduler the container does not hold.
Three options were live and one is disqualified outright.

**An in-memory scheduler is not a candidate**, and §9.6 rules it out in its own
words rather than on taste: "A saga waiting forever for a message that will
never arrive is an order stuck in limbo and a support ticket." A scheduler that
lives in the process loses every armed timeout on the next deployment, which
manufactures exactly that order — and does it silently, because the saga row
survives and looks healthy.

That leaves two durable answers, and they differ in **where the pending timeout
lives**. Quartz with an ADO job store would put it in Ordering's own database,
which is the argument §9.6 already makes for the saga instance one table over —
one database to back up, one migration history, one connection pool. It is the
better answer at scale and it is not the one taken here, for reasons that are
about this platform rather than about Quartz: it is three packages, roughly two
hundred lines of vendor DDL this repository would then own inside its own
migration, eleven `dbo`-prefixed tables cutting across the `ordering.` schema
every other table in this service sits in ([§9.4](../09-messaging.md), §9.6), and a
second set of receive endpoints — because this
platform deliberately does not call `ConfigureEndpoints`, so the scheduler's own
consumers would each need declaring by hand ([§9.8](../09-messaging.md)).

The broker's delayed exchange needs none of that: no package, no schema, two
registration lines, and durability that is already this architecture's model —
§9.8's own failure table says messages queue **in the broker** while a consumer
is down and that the outbox holds them while the broker is. A pending timeout is
a message in flight, and this platform's answer for a message in flight is the
broker.

**It also makes the test and the production registration the same two lines**,
which decided it. The in-memory transport implements the delay itself, so
§12.5's harness runs `AddDelayedMessageScheduler` and
`UseDelayedMessageScheduler` verbatim — the transport differs and the
registration under test does not. A Quartz production path tested over an
in-memory Quartz is a different mechanism wearing the same test.

**Consequences.** The broker image is no longer a stock tag, and that is the
whole of the cost *to deploy* — the cost to **run** is the uncancellable
timeout below, which is larger and is where this decision gets superseded.
The image is pinned to a **minor** (`rabbitmq:4.1-management-alpine`)
because the plugin is built against a broker line and `rabbitmq-plugins enable`
refuses one it does not match — a floating `4` would enable cleanly today and
fail the image build on whatever Tuesday 4.2 becomes latest. The plugin is
`ADD`ed by URL and then checked in the following `RUN` — `sha256sum -c` against
the pinned digest, then `chmod 644` — so a substituted asset fails the build
rather than reaching a broker, and a plugin the broker cannot read fails it too:
`ADD` from a URL lands 0600 and root-owned, `enable --offline` never opens the
archive, and the image therefore **builds cleanly and dies at start** with an
Erlang `eacces`. That was measured, not reasoned.

**Both of those belong on `ADD` and cannot go there**, which is the constraint
worth recording rather than the syntax. `ADD --checksum=` and `--chmod=` are
**BuildKit-only**, and this image is built by two builders: Compose uses
BuildKit, and §12.4's Testcontainers fixture uses the classic `/build` API,
which refuses them outright — *the --chmod option requires BuildKit*. A
Dockerfile only one of the two can build is a fixture that silently falls back
to a stock broker, which is the failure this whole decision exists to prevent.
The digest is therefore verified one layer later, after the file is written
rather than before; nothing reaches a running broker either way. **Do not
"tidy" these back onto the `ADD`** — that re-arms the fallback, and it is the
kind of edit that looks like a simplification.

A broker without the plugin is the failure mode worth naming, and it was
measured rather than reasoned — three earlier drafts of this paragraph each
described it differently and all three were wrong. What actually happens:

| | |
|---|---|
| Bus start | **Clean.** The connection is made, the endpoints declare, readiness reports ready |
| First `.Schedule(…)` | `exchange.declare` fails with `precondition_failed: unknown exchange type 'x-delayed-message'` |
| After that | MassTransit **retries the topology indefinitely**, so the scheduling call never returns and the saga's transition never completes. The broker logs a channel error every few seconds; the service logs nothing and stays healthy |

So the message is neither delivered nor rejected, and the order waits for a
timeout that cannot arrive — §9.6's stuck order, produced by infrastructure
rather than by a missing transition. **Nothing on the service side ever says
so**, which is why §14.1's healthcheck asserts the plugin is enabled as well as
that the broker is running: a stock broker is *healthy* and wrong, and the only
evidence is in a log belonging to something else.

**This scheduler cannot cancel, and that is the cost this ADR most understated.**
MassTransit 8.5.3's `DelayedScheduleMessageProvider.CancelScheduledSend` returns
`Task.CompletedTask` on both overloads — checked against the tagged source — so
once the broker holds a delayed message nothing recalls it. Every `Unschedule`
in §9.6's machine is a no-op here, and **every order keeps all of its timeouts
until they fire**: five minutes, fifteen minutes and ten minutes on the
ordinary path, and three days on every order that ships. The token-id columns
are written and never read back.

Correctness survives it, and by construction rather than by luck — a timeout
arriving in a state that does not handle it is ignored, and one arriving after
the saga finalised is discarded. Both were measured; the first has a test
(§12.5). What does not survive is the volume argument: the plugin keeps its
delayed messages in Mnesia, per node and unreplicated, its own guidance warns
against large numbers of long delays, and this decision guarantees one
undeliverable delayed message **per wait the order enters** rather than the few
a cancelling scheduler would leave — four on an order that ships.

**That number is not fixed by this decision and has already moved once.** It
was three until §9.6 gained `AwaitingConfirmation`
([#126](https://github.com/alexander-shamray/blueprint-backend/issues/126)),
which is worth recording here because the *volume* is this ADR's stated
supersession trigger: a state added to the machine raises the standing Mnesia
population by one message per order, and nothing in the state machine's own
review would surface that. **A cost that grows with a decision taken somewhere
else is one to state as a rule rather than as a total**, which is why the rule
is written above and the four is an illustration of it.

**That is the trigger to supersede**, and the replacement is the Quartz option
above rather than a new one — Quartz cancels, so the `Unschedule` calls and the
token columns start working the day it lands, with no change to the state
machine, its schedules or the tests. Which is the same property that let the
choice be made on cost.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
