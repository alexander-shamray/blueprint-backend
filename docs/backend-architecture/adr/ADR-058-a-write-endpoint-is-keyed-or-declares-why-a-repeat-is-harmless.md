# ADR-058 — A write endpoint is keyed or declares why a repeat is harmless

**Decision.** An endpoint a POST, PUT, PATCH or DELETE reaches is exactly one
of two things. It is **keyed**: it dispatches an `IIdempotentCommand`
([§8.5](../08-caching-redis.md)), which is read off the handler's parameters
or, where the endpoint builds the command from a route value and a request
record, off `Idempotent<TCommand>()`. Or it is **declared retry-safe** with
`RetrySafe(kind)`: `Convergent`, when a repeat of the same request leaves the
state the first one left and is answered as the first one was, or `ReadOnly`,
when the endpoint writes nothing. An endpoint that is neither, or both, or
that declares both kinds, fails the build. So does a keyed endpoint that
allows anonymous callers or names no authorisation, which is §8.5's subject
rule reaching the declared form, and so does an idempotent command no
endpoint reaches. The declarations are `Common.Web`'s. The gate is
`WriteEndpointRule` in `tests/Common.TestSupport`, and each host that maps
handlers — Catalog, Ordering, Inventory, Payments and the BFF — holds it in a
`WriteEndpointRuleTests` beside a floor naming every write the host maps and
every endpoint the selection leaves out. The four services' suites hold
[ADR-057](ADR-057-a-command-id-is-bound-to-the-fingerprint-of-the-command-that-claimed-it.md)'s
member rule beside it, since a keyed endpoint is protected only as far as its
command's fingerprint reads the request: `CommandFingerprintRule` names a public
field, a property marked `[JsonIgnore]`, a member whose type exposes no public
property, a member declared as a type that is not sealed, and a collection whose
type promises no order, in every idempotent command the service declares. A gRPC
method is a POST ([§9.7](../09-messaging.md)) and is inside the rule, declared
on the builder `MapGrpcService` returns.
**Why.** A client whose answer is lost sends the request again, and what the
repeat does is a property of the endpoint that somebody has to decide. §8.5
opened by saying every non-idempotent write command carries a `CommandId`,
and nothing held an endpoint to it: the only gate read commands that already
carried the field, so a write that carried none was unprotected and no test
noticed. A declaration is metadata on the route, where the gate reads it and
a reviewer reads the reason beside it; a keyed endpoint needs none, because
its handler's signature already says so.
**Consequences.** Every new write endpoint is two edits its author cannot
skip: the binding or the declaration, and its name in the host's floor.
`Convergent` has a limit. A repeat that arrives after a different write is
not a repeat of the present state: a stale `SetOnHand` re-applies an old
count, and a stale release undoes a reinstatement. `Convergent` is the right
declaration where that interleaving cannot occur or is an operator's
deliberate act; where a stale repeat would undo a later write that matters,
the endpoint is keyed instead. `CancelOrder` has no such interleaving,
because cancelled is terminal ([§5.4](../05-tactical-ddd.md)).
`ReleaseReservation` and `SetOnHand` are operator acts behind
`InventoryPermissions.Admin`, and the residual is accepted for them. A
declaration is a claim and nothing checks it: the gate reads that an endpoint
said `Convergent`, not that it converges. A declaration on a route group or
on a gRPC service reaches every endpoint under it, the one added later
included, and the host's floor is what turns that addition into a failed
test. Two hosts are outside the rule and hold no suite for it: the gateway
maps a reverse proxy and no handler of its own
([§10.1](../10-api-gateway.md)), and Shipping's worker exposes no API
([§3.2](../03-bounded-contexts.md)) and serves
[§13.5](../13-observability.md)'s probes alone. A handler mapped in either
brings the suite with it, and a worker
[§4.5](../04-solution-structure.md)'s scaffold renders is born with it. A
command reached only through a consumer is outside the rule as well:
[§9.5](../09-messaging.md)'s inbox deduplicates it, so it declares no
`IIdempotentCommand`, and one that does fails the gate above. An endpoint
that names no method accepts every verb, and the selection reads it by how it
was mapped. With a handler it is a write. As a bare `RequestDelegate` it is
not, because that is how the framework maps §13.5's probes and gRPC's
unimplemented-method fallbacks and nothing else marks those; the host's floor
names each endpoint left out this way, so a route a host maps in that shape
fails the floor rather than passing unread. A command bound inside a
parameter object is not seen as keyed until its endpoint says
`Idempotent<TCommand>()`, which fails closed. The member rule reads the types a
command's members are declared as, so it is stricter than the serialiser
wherever it cannot see past one. It refuses a member declared `object`, which
ADR-057 hashes by its runtime type; a class that is not sealed, whether or not
anything derives from it; and every collection but an array and the list types
whose order is their contract, so a dictionary and a bare `IEnumerable<T>` are
refused with a hash set. The way out is a narrower declared type. It reads a
type a converter would write as that type's properties, not as the converter
writes it, and it has no floor of its own: the commands it reads are the ones
the gate compares with the endpoints. `Common.TestSupport` gains a reference to
`Common.Web` for the two metadata types, and two suites gain one to
`Common.TestSupport` for the gate: the BFF's, and `Common.Web.Tests`, which
holds the gate's own.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
