# ADR-094 — A holder reports its erasure by sending to Privacy's queue, and does not publish it

**Decision.** `PersonalDataDeleteCompleted` is a message a holder **sends** to
`privacy-completions`, the queue Privacy owns, and not an event the holder
publishes. It amends [ADR-092](ADR-092-privacy-is-a-seventh-service-and-its-responder-set-is-fixed-when-a-request-is-raised.md),
which left the choice open and read as a broadcast. The contract stays in
`Common.Contracts.Privacy.V1`, beside the commands a queue's owner accepts, and
each holder names the queue in its own `Endpoints`, as Ordering names
`payments-commands`. The send is made from the consumer of the request, so the
receive endpoint's in-memory outbox ([§9.8](../09-messaging.md)) holds it until
the consumer has succeeded. A crash between the erasure's commit and that
release loses the send, and the request goes overdue and is reissued, which is
the outcome ADR-092 already gives a missing answer.

**Why.** A completion is a reply to one party, and the broker's accounts are
drawn so that a reply of this shape has no other home. A service may write only
its own context's contracts, because one that can publish a peer's events can
forge them ([ADR-036](ADR-036-the-broker-has-a-per-service-identity.md)), and a
service with no Domain project writes no contract exchange at all. Five
holders publishing one message in Privacy's namespace breaks the first rule,
and Notifications and the BFF, which have no Domain project, break the second.
A send to a peer's queue needs the queue and nothing else, and the gate already
derives it from the sender's `Endpoints`.

**Consequences.** The holder's name in the message is its own claim. Nothing
at the broker says who sent it, where a publish in the sender's own namespace
would have said, so Privacy checks the name against the request's stored
responder set and flags a name outside it. A repeat answer from one holder is
counted once, as a reissue makes every holder answer again. A send is not
staged with the erasure's commit as an outbox row is, so the atomicity ADR-092
described for the three services with an outbox is given up for the sake of
one mechanism for all five. `PersonalDataErasedDomainEvent`,
which Common.Domain's audit record raises for a mapper to publish, has no
consumer and is removed with the first holder's pull request.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
