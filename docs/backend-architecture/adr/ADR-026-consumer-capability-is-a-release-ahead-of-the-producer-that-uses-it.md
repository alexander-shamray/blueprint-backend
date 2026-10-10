# ADR-026 — Consumer capability is a release ahead of the producer that uses it

**Decision.** Anything a consumer must be able to *recognise* — a new message
type, a new binding on an existing endpoint, a new member of a closed
vocabulary — is deployed everywhere before the release that starts emitting it.
The two go out as two ordinary releases in an order, never as one.

Where the consumer and the producer are in the same build and the queue is
shared, ordering the *deploy* separates nothing, so the change is split across
two *releases* before there is an order to impose: one that declares the
consumer and publishes nothing new, then one that publishes. The alternative
is a cutover with no overlap, which is not a canary and should not be called
one.

**Retirement is the mirror and takes the opposite order.** A consumer removed
before the producer stops emitting leaves the binding live — MassTransit does
not unbind a queue when a consumer is deleted — so every subsequent message is
skipped, permanently rather than for the length of a rollout. Producer
capability retires a release ahead of the consumer, which is [§9.2](../09-messaging.md)'s
own deprecation-window rule read in the direction it does not spell out.

`<queue>_skipped` is alerted on ([§13.6](../13-observability.md)), which is what
makes this a rule rather than advice.

**Why.** [§9.2](../09-messaging.md) said additive changes need no version bump
because "consumers deserialising an unknown field ignore it" — true, and true
only of fields. A deserialiser is built to skip an unknown field. Nothing is
built to skip an unknown *message type*, and a closed vocabulary is a
whitelist by construction, so both fail while looking additive.

**The two failure modes are opposite, and the quiet one is the reason this is
an ADR.** A new binding on a shared queue: the broker hands the message to a
replica whose build declares no consumer for it, MassTransit parks it in
`<queue>_skipped`, nothing throws, and [§13.6](../13-observability.md) watched the
*error* queue only. A new vocabulary member: the mapper refuses a code it does
not know and [§9.8](../09-messaging.md) deliberately excludes
`ContractMappingException` from retries, so a well-formed escalation from a
newer producer reaches the error queue on the first attempt. One loses the
message in silence and the other pages immediately, and a single rule fixes
both because the root is one — a producer shipped in the same release as the
consumer that understands it.

**The blueprint reasoned about rolling deploys in a dozen places and none of
them was this.** §6.5's live cache key, §7.3's migration race, §8's key rename,
§9.4's `MessageTypeMap` alias, §15.5's backward-compatible migrations — every
one is a **state** compatibility rule, about a schema, a key, a persisted type
name or a persisted payload. Not one is about **routing** or **vocabulary**,
which is exactly why both instances fell through a pattern that otherwise looks
complete. **A gap inside a well-covered pattern is harder to see than a gap in
an uncovered one**, because the neighbouring cases make the topic feel
answered.

**Consequences.**

**A binding change that also starts publishing costs a release, and the canary
cannot absorb it.**
[ADR-022](ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md)
makes the canary a second release of the same chart answering the same
Service, so both tracks consume the same queue for the length of the ladder —
roughly half an hour at the ladder's current dwells. The consumer and producer of a saga event
are in the same build, so ordering the *deploy* separates nothing — for the
length of the ladder the old track is bound to neither. What separates them is
splitting the change across two *releases*, which is the usual answer and a
real cost, paid per binding.

**This is an ordering constraint and not a coordinated deploy**, and the
distinction is the one §9.2 closes on. A lockstep deploy is what this
architecture exists to avoid; two independently shippable releases that must go
out in a known order is what every expand/contract migration already is. If the
two ever cannot be separated, that is a signal the boundary is wrong rather
than a reason to relax the rule.

**Nothing gates the ordering itself, and the alert is what stands in.** No CI
check can know that a consumer is deployed everywhere — that is a fact about
the cluster and not about the branch. What `SkippedQueueDepth` gives is
detection rather than prevention: a violation pages within a minute instead of
being discovered when someone asks where an order went. **A rule whose
enforcement is a page rather than a build failure is weaker and is worth
having**, on the same terms as ADR-024's guarantees, which no gate holds
Inventory to either.

**It also fires on a genuinely missing binding**, which is a different fault
with the same symptom. The runbook separates them by asking whether every
replica is on the same build; the alert cannot, and says so.

**The detection is owed a deployment this repository configures only for
Compose, and saying so is the point.** `rabbitmq_queue_messages` carries a
`queue` label only where the broker runs `rabbitmq_prometheus` with per-object
metrics enabled and something scrapes it. §14.1's image enables both and its
collector scrapes port 15692 into the LGTM container, which is what lets the
alert be seen to fire locally. The cluster's broker and scrape are not in this
repository, and §13.7 already states that nothing here deploys Prometheus. So
the rule above is enforceable *by an operator who has wired that up*, and is
advice until then. `ErrorQueueDepth` has carried the same dependency since
PR-24 without anyone writing it down — the difference is that this ADR leans a
**specification rule** on it, which is a heavier claim than an alert makes,
and a claim of enforceability that quietly depends on an unwired signal is the
exact shape of *a registered name is not a live signal*.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
