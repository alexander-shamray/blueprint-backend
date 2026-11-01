# ADR-192 — Fault reads stop at consumed types (#1702), amending ADR-190

**Decision.** Each account's `read` on the fault exchanges names
`MassTransit:Fault--<namespace>:<type>--`, closed at its trailing `--`, for
each `Common.Contracts` type its Messaging code takes as a type argument, and
its own Messaging namespaces whole, and no other.
`deploy/compose/rabbitmq/check_permissions.py` holds it: `consumed_types`
derives the types, a name two contexts declare counting in each the service
references, and check 3d owes those fault exchanges and refuses, in every
contract namespace and every peer's Messaging namespace, an invented type and
each declared type the account does not consume.
`tools/new-service`'s render cuts the template's one consumed type with its
subscription, so a rendered service reads the faults of its own Messaging
namespace alone.
**Why.** A fault exchange is named for one message type, and
[ADR-190](ADR-190-holders-send-to-privacys-exchange-and-fault-reads-stop-at-consumed-namespaces-1682-1683-amending-adr-036-and-adr-094.md)
narrowed the `read` on them only to the namespaces an account consumes. A
namespace holds messages that different services consume, so each erasure
holder, consuming `PersonalDataDeleteRequested`, read the fault exchange of
Privacy's own completions consumer, could bind a queue of its own there and
received the other holders' completions whole whenever that consumer faulted
on them; a pure consumer read the faults of a peer's command consumer the
same way. A fault's publish binds from `MassTransit:ReceiveFault`, which
every account reads, and from the fault exchange of the type its consumer
took and no other per-type one, so nothing a service runs needs more than
its own types. Its own Messaging namespaces stay whole because no peer may
consume their messages, which check 5 already refuses.
**Consequences.** The grants name each consumed type and grow longer, and a
consumer added for a type the account did not consume before fails the gate
until the grant names that type's fault exchange, where under ADR-190 it
passed whenever the namespace was already consumed. Two services consuming
one type each still read the other's fault exchange for it: the fault carries
a message both already receive, with the peer's exception and host, and that
residual is accepted. The derivation reads every type argument rather than a
list of consumer shapes, so a type the Messaging code names without
consuming it is owed a `read` too; that errs towards a message the service
already handles, where a missed shape would leave a fault refused and the
service silent. A test bus that runs under a service's account and faults a
type the account does not consume still sets `PublishFaults` off, as
ADR-190 says, now for a type rather than a namespace.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
