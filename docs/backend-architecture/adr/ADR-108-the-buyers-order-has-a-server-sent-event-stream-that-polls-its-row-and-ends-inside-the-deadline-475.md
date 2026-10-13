# ADR-108 — The buyer's order has a server-sent event stream that polls its row and ends inside the deadline (#475)

**Decision.** `Web.Bff` serves `GET /v1/orders/{id}/events` beside
[§10.7](../10-api-gateway.md)'s two reads, as `text/event-stream`. It is
authenticated and owner-checked exactly as the detail route is: the subject
is the principal's, and another buyer's order answers the same 404 an unknown
one does. Each stream **reads the order's row on a fixed interval** and writes
an `order` event, whose data is the detail route's `OrderDetail` and whose
`id` is the row's `AsOf`, when that value differs from the last one it sent.
Nothing is shared between streams and nothing is held between requests: no
broker fan-out, no in-memory registry of watchers, no notification from the
projection's consumer, so which replica holds a stream and which replica
consumed the event do not matter. A comment line is written when nothing has
changed for the heartbeat interval. **A stream ends by itself inside the
host's deadline**, `ServiceOptions.OperationTimeout` less a margin, so it
opts out of no request-timeout layer and the gateway route is unchanged; it
ends at once on a delivered order, which nothing follows. The client
reconnects, which `EventSource` does without being asked, and a reconnect
carrying `Last-Event-ID` equal to the row's `AsOf` is sent nothing until the row
moves. The stream is a convenience over the read and never a second source:
a client that misses an event reads the row, and the first event of a stream
is the row as it stands.
**Why.** Polling with a stated interval was a legitimate answer, and the stream
costs more than it. It is taken because the client asked for it, its
order-placed screen is the one consumer and the route only adds to `v1`, and the
cost can be made small. Three replicas make a push design a distribution
problem: the projection row is shared and a notification of its change is not,
so a stream fed by an in-process event is fed only when its replica consumed it.
A broker fan-out to every replica fixes that by giving each a queue, a consumer
and a registry of its watchers, which is in-process state the replica count
multiplies. [§10.3](../10-api-gateway.md) accepts that for a best-effort limiter
and for nothing a client reads as an answer. A short poll of one row by primary
key, in a stream that is itself the only state, has none of that, and costs one
indexed read per open stream per interval. Ending inside the deadline is the
other half. §9.7's timeouts decrease inwards and
[ADR-066](ADR-066-a-request-past-its-hosts-deadline-is-answered-504.md) allows
an opt-out, but an opt-out here would need the BFF's, the gateway's and the
ingress's idle and read timeouts all lifted for one route, three places a later
edit can undo. A stream shorter than the shortest of them needs none of the
three, and its token is checked again at every reconnect rather than held for an
hour past its expiry.
**Consequences.** A watching buyer costs a request per lifetime, so the
gateway's per-request limiter ([§10.3](../10-api-gateway.md)) bounds them with
no new policy: three or four reconnects a minute is far inside its bucket.
Updates reach a stream one poll interval after the row moves, on top of the
projection's own lag, and the event's `asOf` is the BFF's clock at the row's
write, as in the read. The BFF holds a connection and a pooled SQL connection
per open stream for the length of the poll and none between polls, so the
connection pool's size, not the replica count, is the ceiling on watchers;
an adopter with many more watchers than the pool serves has outgrown a poll
and owes the broker fan-out, which this record does not forbid and does not
build. The heartbeat is a comment, which a proxy that buffers a response
until it is full would defeat, and the response says `X-Accel-Buffering: no`
and `Cache-Control: no-store` for the ones that honour them.
§10.7 gains the route and its contract, and the one consumer builds against
it as it did the reads.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
