# ADR-190 — Holders send to Privacy's exchange and fault reads stop at consumed namespaces (#1682, #1683), amending ADR-036 and ADR-094

**Decision.** A holder sends `PersonalDataDeleteCompleted` to
`exchange:privacy-completions`, not `queue:privacy-completions`, and its
broker account keeps `configure` and `write` on that name and loses `read`;
Privacy's own receive endpoint declares the queue and binds it. Each account's
`read` on the fault exchanges, `MassTransit:Fault--<namespace>:<type>--`,
names the namespaces it consumes, which are the contexts its Messaging code
names, its own context and its own Messaging namespaces, and no other.
`deploy/compose/rabbitmq/check_permissions.py` holds both: check 1b owes an
`exchange:` send its `configure` and `write` and refuses its `read`, a queue
two services send to by `queue:` fails the gate, and check 3d refuses `read`
on the fault exchange of any contract or private namespace the account does
not consume.
**Why.** A RabbitMQ permission cannot tell a queue from an exchange of the
same name, and a `queue:` send binds the queue, which takes `read` on the
exchange.
[ADR-036](ADR-036-the-broker-has-a-per-service-identity.md) accepted that for
a peer's command queue with one sender, where the reader can swallow only its
own commands. Five holders send to Privacy's queue, so each could consume and
ack the others' completions, and those erasures would go unrecorded and
overdue while a completion forged in another holder's name closed its part.
An `exchange:` send declares the exchange and publishes to it and binds
nothing, so it is owed no `read` at all. The fault exchanges are the same
boundary one exchange over: a fault carries the message that faulted, and a
fault's publish needs `read` only on the fault exchanges of the types its own
consumers take. A `read` covering every `Fault--` let one credential bind a
queue of its own to another context's fault exchange and receive its messages
whole, the cross-context `read` check 5 already refuses on the contract
exchanges themselves.
**Consequences.** A completion sent while Privacy's queue has never been
declared is dropped by the exchange, where a `queue:` send would have
declared the queue and kept it. A completion answers a request Privacy
raised, so Privacy has declared and bound its durable queue first, by starting
its endpoint or, while a switch holds that endpoint off, through
[ADR-145](ADR-145-a-receive-endpoint-off-at-start-has-its-topology-declared-by-a-bus-that-only-deploys-it-1360-amending-adr-144-and-adr-026.md)'s
topology-only bus; a broker rebuilt from nothing while holders still owe
answers loses those
answers, and the requests go overdue and are reissued, the outcome
[ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md)
gives a missing one. The five holders' erasure suites declare Privacy's
endpoint first with a topology-only bus under Privacy's account, so
`Common.TestSupport` references the transport package. `configure` on the
name still reaches the queue, so a holder could delete it: ADR-036's
`configure` residual, unchanged. A service that starts consuming a context
owes that context's fault exchanges a `read`, and the gate fails until the
grant names it, and a test bus that runs under a service's account and
faults a message of its own type sets `PublishFaults` off, since that
type's fault exchange is in no namespace the account reads; the gate reads
only `src/`, so the refusal shows only as that bus hanging. **The narrowing is per namespace, not per type**: a fault
exchange is named for one message type, and an account still reads the one
a peer's consumer publishes for a message in a namespace both consume, so
each holder, consuming `Common.Contracts.Privacy.V1`, reads the fault exchange
of Privacy's own completions consumer. Narrowing to the types consumed is
#1702. A peer command queue with one sender keeps its `queue:` send and the
`read` that comes with it.
[ADR-094](ADR-094-a-holder-reports-its-erasure-by-sending-to-privacys-queue-and-does-not-publish-it.md)'s
send is otherwise unchanged.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
