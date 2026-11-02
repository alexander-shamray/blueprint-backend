# ADR-193 — The fulfilment saga's stop is this machine's CPU (#1714)

**Decision.** [§9.6](../09-messaging.md)'s fulfilment saga keeps its receive
endpoint as it is, with no prefetch or concurrency limit of its own, which
leaves MassTransit's default, a prefetch the broker reads as 48 a consumer on
the stress stack, and keeps its statements: from 2400 VUs its handled rate
stops near 195 messages a second while its queue grows, and that stop is
recorded as the reference machine's CPU, not a limit of the saga. Nothing is
tuned.
**Why.** Run 20261102-143055 at `e9f7c0229` took the plan of 20261101-171739
(two machines, fresh volumes, a 2400 s warm-up at 800 VUs, 180 s holds at
800, 1600, 2000, 2400, 2800 and 3200 VUs) to the end with no request failing.
Query Store in Ordering's database, read by the minute, runs the saga's
`SELECT TOP(2)` on `OrderFulfilmentStates` once for each saga message, so its
count is the saga's handled rate: about 95, 145, 170, 195, 197 and 191 a
second at the six steps. Orders placed in a hold rose from 6 255 at 2400 to
7 491 and 8 431, and the saga queue, which ended 2400's hold at 113 messages,
went from 2 178 to 3 682 over 2800's second half and from 7 834 to 11 898 over
3200's. Each message's statements did not cost more: about 0.48 ms of
duration and 0.22 ms of CPU at 2400, 0.42 ms and 0.23 ms at 3200, and
Ordering's whole database used 53 to 56 s of CPU a minute at 2400 and 57 to
61 s after.
What stopped rising was the host. Ordering.Api's three replicas, with no CPU
limit, each ran at 116 to 126% of a core from 2000 VUs on, while the host
stood at 94% at 2000 and 95 to 97% after; run 20261101-171739, before
[ADR-189](ADR-189-saga-inbox-cleanup-runs-on-one-replica-and-yields-to-lock-waits-1671-adding-to-adr-182.md),
shows the same shape, its replicas at 114 to 128% with the host at 90 to 95%.
A saga change has no measured cost to remove: its statements' time, their
lock waits included, did not grow where its rate stopped, and ADR-189
cutting the inbox cleanup's lock wait by 3.5 to 3.8 times at those steps left
the queue no shorter.
**Consequences.** What a saga message spends outside its statements was not
read: how many deliveries a replica held in flight, how long each took in the
consumer, and its share of the commit's log flush, which on this
stack's SQL Server averaged 5.6 ms of `WRITELOG` and 13.9 ms of
`PREEMPTIVE_OS_FLUSHFILEBUFFERS` a wait since the container started. The
queue's acked rate and in-flight count are not in the stress summary, and
the run's Prometheus series were gone before this read, so the handled rate
here is counted from a statement, not the broker. The stop is this machine's:
a host with more cores, or a database off Docker Desktop's disk, moves it, and
says nothing about production. Two runs of nearly the same code ended 3200's
hold at 6 911 and 11 898 queued, so a step's figure is good to about a factor
of two. A run whose saga rate stops while the host has CPU to spare, or whose
per-message cost rises, reopens this.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
