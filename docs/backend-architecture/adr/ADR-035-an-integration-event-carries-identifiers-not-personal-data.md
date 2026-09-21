# ADR-035 — An integration event carries identifiers, not personal data

**Decision.** An integration event published to every interested consumer
carries identifiers, not personal data — [§11.7](../11-identity-authorization.md)'s
rule, applied without exception to the contracts in `Common.Contracts`.
`OrderConfirmed`'s `ShippingAddress` is removed and `ShippingAddressV1` is
deleted. How Shipping obtains a delivery address is left open and belongs to
Shipping's own PR.

> **This is not a claim that the events are anonymous, and an earlier draft of
> this record read as one.** `OrderConfirmed` still carries `CustomerId`, and a
> resolvable pseudonymous identifier is personal data under GDPR Art. 4 — as
> are order details linked to it. §11.7 says so itself one rule further on,
> where erasure at the owning service means "customer identifiers replaced".
> So the decision is narrower than "no personal data on the wire": what a
> broadcast contract may not carry is **directly identifying or free-text**
> personal data — a name, an email address, a postal address — the fields whose
> only remedy is to reach every copy and delete them.
>
> **What makes the identifier tractable is where its resolution lives.**
> `CustomerId` means nothing without the service that can turn it into a
> person, so severing that link once, in the one store that owns it,
> de-identifies every copy downstream without the erasure choreography ever
> having to reach the broker, an outbox row or a consumer's own store. That is
> the
> whole of why §11.7 tells a contract to carry the identifier rather than the
> value, and this ADR is that rule applied rather than a stronger guarantee
> laid over it.
>
> **The residual is real and is not closed here.** An event stream still
> carries linkable identifiers, so a consumer that independently resolves
> `CustomerId` re-creates the problem in its own store, and §11.7's rules for
> consumers are what bind it — unbuilt, like the rest of that section.

> **This removal may not ride a rolling deployment, and that is
> [ADR-026](ADR-026-consumer-capability-is-a-release-ahead-of-the-producer-that-uses-it.md)'s
> rule rather than a new one.** `ShippingAddress` was `required`, and
> `System.Text.Json` refuses a payload missing a required member — the same
> mechanism §9.2 records in the *adding* direction, where a new build faults
> every message an old build staged. Retirement is its mirror. Ordering is
> itself a consumer here: §9.6's saga binds `Event<OrderConfirmed>`, so it
> deserialises the whole contract even though its transition reads only
> `OrderId`. During an overlapping rollout a new replica's `OrderConfirmed`
> reaches an old replica that still declares the member, the message faults
> through §9.8's retries into the error queue, and §13.6 pages.
>
> **So the change takes one of the two shapes ADR-026 already allows**: a
> cutover with no overlap — which that record is careful to say is not a canary
> and must not be called one — or a two-release retirement, the first making
> the member optional everywhere and the second removing it. Either is a
> deployment decision rather than a code one, which is why it is recorded here
> and not solved above.
>
> **It is taken in place now because there is nothing deployed to break.** No
> cluster has ever run this platform — the charts, the alert rules and the
> canary are all artefacts no cluster has seen — so there is no old replica to
> hand a reduced payload to, and the cheapest moment to change a contract's
> shape is before anything runs it. **That is a fact with an expiry date**: the
> first deployment ends it, and after that a comparable removal owes one of the
> two shapes above. Recorded rather than left implicit, because "no consumer"
> was true of *other services* and was never true of Ordering.

**Why.** §9.1 argued the address onto the contract on "fat enough" grounds —
Shipping cannot act without one and should not call back to get it — while
§11.7 named an `OrderConfirmed` carrying personal data as *the* counter-example
of what must never happen. Two chapters stated opposite things about one
contract, so a reviewer reading either alone concluded the rule held.

The rule is the one that wins, because the address is unreachable once
published. It is serialised into `ordering.OutboxMessages`, whose retention
purge deletes only rows with `ProcessedAt IS NOT NULL` — deliberately, so that
abandoned rows survive for §13.6's alert — so an abandoned row keeps the
payload indefinitely, and a test pins that it does. It sits in the broker, for
which no chapter specifies a retention bound. And it reaches whatever each
consuming service persists from it — not the inbox, which stores a message id,
an endpoint and a time and no payload, but any projection, read model or log
that keeps what arrived — with §3.2 giving `OrderConfirmed` to Notifications,
which has no use for an address at all. §11.7's erasure choreography reaches
none of those surfaces, and §13.4's redactor matches on key names, none of
which cover an address.

**Removed rather than versioned**, on §9.2's own carve-out: where the point of
a change is that a value must not be on the wire, publishing V1 alongside V2
keeps the offending shape consumable for the length of the window, so the
standard remedy would re-arm the defect. §9.2's second condition is met by this
record existing, so that a later reader can check rather than take it on trust.

**Its first condition — that no service consumes the version — was not met,
and an earlier draft of this paragraph said it was.** Shipping and
Notifications are unbuilt, so no service on an independent release schedule
could be stranded; but §9.6's saga binds `Event<OrderConfirmed>`, which makes
Ordering a registered consumer, and a bound consumer deserialises the whole
payload however little its transition reads. "The saga reads only the
`OrderId`" is true and answers a different question.

**So the justification is not the condition; it is that nothing is deployed.**
No cluster has ever run this platform, so there is no old replica to hand a
reduced payload to — and the rollout callout above records what that costs
later, in full, because the exemption expires at the first deployment while the
rule does not. Stating the condition as met would have hidden a live-rollout
fault behind a rule about version bumps, which is exactly what that callout
exists to prevent. [ADR-028](ADR-028-a-money-movement-command-carries-no-subject.md)
remains the worked precedent for the removal itself, on the same reasoning:
removing the field removes the possibility rather than guarding against it.

**Consequences.** Shipping's PR inherits an open question and the context to
answer it: an explicit, auditable read back to Ordering, recorded as the
ADR-017 exception a second synchronous hop has to be, or a despatch-time lookup
against whatever store owns the address by then. Choosing now, before a
consumer exists to state what it needs, is the guessing §9.1's "ask the
consumers" rule refuses. `OrderConfirmedDomainEvent` keeps its `Address` and is
untouched: it never crosses a service boundary, and `ordering.Orders`
legitimately stores the address of the order it belongs to — §11.7 governs what
travels, not what a service holds about its own data.

> **The open question is answered by
> [ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)**,
> which takes the read back to Ordering and moves it out of the consumer, so
> that it is a worker's call and not the ADR-017 exception expected above.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
