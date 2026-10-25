# ADR-153 — The deployed BFF runs the assistant on a dedicated single-replica release, web-bff-assistant, behind its own gateway route (#1451), amending ADR-151

**Decision.** A deployed BFF turns the assistant on by running it in a second
release of the BFF image, the chart `web-bff-assistant`, with one replica, no
autoscaler, no disruption budget and a `Recreate` strategy, and the main
`web-bff` release keeps `Assistant:Provider` off. The assistant release is not
on §15.5's canary track, whose second Deployment would run a second pod. The
gateway sends `/bff/v1/assistant/{**catch-all}` to that release's own Service
and everything else under `/bff/v1` to `web-bff`, as it does today, so §10's
sentence that a BFF route needs no gateway change gains this one exception.
The assistant release is the only chart that carries the provider: an
`assistant` capability in the common library chart, off by default and refused
anywhere but there, with the provider's address as a chart value, the
credential as a Secret reference and never a value, and the network policy's
egress to the provider declared as `paymentProvider`'s is. It runs the
`web-bff` host, so it uses that host's client, database and broker accounts and
adds none: the library's allow-lists for client credentials widen to name it,
it takes no object store, and Catalog's network policy admits it for the
pricing hop `get_product` makes. The design is this record; the pieces that
make it true are filed as issues under #1451.
**Why.** [ADR-151](ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-off-by-default-and-scoped-to-the-caller-1347-amending-adr-053-adr-149-and-adr-150.md)
accepted that a conversation lives in the memory of the process that opened
it, and the main release runs three to twenty replicas behind a Service with
no affinity, so a customer's next request lands elsewhere and answers 404 for
a conversation that replica never held. Three ways out were open. A store
shared across replicas is not a store swap: a conversation holds a running
turn, its event ring, its pending client tool calls and the cancellation that
ends them, all live objects of one process, so sharing the state means
redesigning the core and not adding a backend. Affinity at the gateway needs a
destination per pod, and the gateway's destination is one Service address;
making it many is a service discovery change for one feature. A release that
is routed the assistant's paths alone is the only choice that changes no code
in the core or the host: the host serves only its status route while the
assistant is off, and the same image with the provider on serves the rest. One
replica is what makes in-memory correct, and the assistant's routes then cost
the main release nothing.
**Consequences.** The assistant has no availability to speak of. A restart, a
rollout or a node drain ends every open conversation, which a client sees as
404 and recovers from by opening another, and the customer's text is gone with
it, the better side of
[ADR-152](ADR-152-the-assistant-core-sweeps-idle-conversations-on-a-timer-so-idletimeout-plus-a-quarter-bounds-retention-1457-amending-adr-151.md)'s
retention. `Recreate` and the canary exemption are what keep a rollout from
running two pods, and a replica count above one would reintroduce the failure,
so the chart refuses it. The release is a deployable the pipeline must know: a
chart, a descriptor under `deploy/canary/deployables`, an entry in the platform
chart, the gateway's second cluster and its egress entry, and a smoke case for
each, with the check that every service chart renders one disruption budget
made to except this one. Nothing in the descriptor model refuses two charts
that share an image or a source, so it needs no change. The route needs the
gateway's deadline and the stream's lifetime to agree, as they do for the main
route. Capacity is one pod's, and the day that limit binds is the day a shared
store is designed, which this record does not pre-empt. The follow-up issues
carry the chart and its guards, the gateway route, the documents that name the
key and the processor, and §12.7's third-party test for the provider call.
ADR-151 is otherwise unchanged; this decision is read beside it, as an ADR is
amended and never rewritten.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
