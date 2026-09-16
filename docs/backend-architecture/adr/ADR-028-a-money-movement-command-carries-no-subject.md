# ADR-028 — A money-movement command carries no subject

**Decision.** A command that crosses the broker into a decision about whose
money moves carries **no subject identifier**. The service that owns the
decision resolves the subject from its own record, built from an event whose
subject was bound from a principal.

Concretely: `AuthorisePayment` is
`(Guid OrderId, decimal Amount, string Currency)`. Payments consumes
`OrderPlaced` ([§3.2](../03-bounded-contexts.md)) and keeps its own record of the
order — **the payer, the total and the currency**, all three of which that
event carries — then resolves the payer from that record when the command
arrives and checks the command's amount and currency against it. `Amount` and
`Currency` stay on the command. Ordering's saga instance drops its `CustomerId`
too, so the value is not available to a later transition that might put it back
on a message.

**Why.** §11.4's subject rule — *a subject identifier is bound from the
principal, never from the request* — excluded the message path, because a
command arriving over the broker has no principal to bind from. That exclusion
was recorded as an open question rather than a decision, and it left
`AuthorisePayment` naming the customer whose instrument Payments would charge
in a field nothing on the receiving side could check
([#63](https://github.com/alexander-shamray/blueprint-backend/issues/63)).

The rule that closes it is not "bind on the message path" — there is nothing to
bind from — but **re-derive**, and what makes re-derivation available is that
the subject is already written down somewhere else. `OrderPlaced` carries a
`CustomerId` bound from the principal at Ordering's endpoint, so a service that
consumes it holds the same fact from a party that authenticated somebody,
rather than from a sender that merely asserted one.

**That is a statement about the legitimate path and not about the event's
provenance**, and the difference is the residual this ADR closes with: nothing
authenticates an `OrderPlaced`, so the shared broker principal
([#44](https://github.com/alexander-shamray/blueprint-backend/issues/44))
can forge one and seed a payer of its choosing. What this decision buys is that
the **command** no longer offers a payer-selection field — a narrowing whose
exact reach is argued below. Calling the event-backed value *trustworthy* would
be claiming authenticated provenance the platform does not have until #44 or
event signing exists, and an earlier draft of this paragraph did.

**Not every field is the same question, and the line between them is
instruction versus authority.** `Amount` and `Currency` say *what to do*. The
sender decides them, so they must travel, and Payments may compare them against
its record and refuse a mismatch — a consistency check between two parties who
both have a view. A subject says *on whose behalf*, and that is not the
sender's to state: it is the deciding service's to derive.

**The reason is not that only the subject is uncheckable, and getting that
wrong is instructive.** An earlier draft of this ADR argued exactly that — a
field the receiver can check is a claim, one it cannot check is an assertion —
and the record this decision *itself introduces* refutes it. Payments stores
the payer along with the total, so a supplied `CustomerId` would be as
checkable as the amount; checkability separates none of the three.

What survives is stronger than the argument it replaces. **A transported
authority is a second source for a decision that must have exactly one.** The
check that would catch a mismatched subject is a check somebody has to
remember to perform, and a redundant authority-bearing field is precisely the
one a later code path reads *instead of* deriving — cheaper at the call site,
identical in the happy case, wrong exactly when it matters. Removing the field
removes the possibility rather than guarding against it.

**So a money-movement command carries its instruction and never its
authority**, and that is the form to apply to the next such contract.

**The precedent is one service over, and the closer of the two is §6.4's price
projection.** `PlaceOrder` reads `ordering.ProductPrices` — Catalog's price
events projected into a table Ordering owns, behind an `IProductPriceReader`
documented as *never a remote call* — so a handler needing another service's
fact on the deciding path looks it up locally. Payments' lookup is the same
shape: write path, at the moment of decision, against a record built from
events. [ADR-027](ADR-027-the-order-summary-stores-product-ids-and-resolves-the-name-locally.md)
is the same mechanism on the **read** path and for product **names**; the two
tables are distinct and `ordering.ProductPrices` has never carried a name.

Only the thing the local copy buys differs: there a synchronous hop avoided,
here an unverifiable assertion removed.

**Consequences.**

**The field is removed from `V1` in place, and [§9.2](../09-messaging.md) is what
that has to be reconciled against.** Removing a field is a breaking change and
the standing rule is a new version with both published for a deprecation
window. The exception taken here is the one §9.2 now states: `Payments.V1` has
no consumer — Payments is unbuilt, and no service in the solution consumes
`AuthorisePayment` — so there is nobody for a window to serve.

**"No consumer" and "nothing deserialises it" are not the same claim**, and an
earlier draft of this paragraph made the second, which is false: §12.6's
contract suite round-trips every contract through the bus serialiser, this one
included. A test that asserts a shape is not a consumer bound to it — it moves
with the contract in the same commit, which is exactly what a deprecation
window exists to make unnecessary. The condition §9.2 states is about
*services*, and it is worth keeping the two apart, because the wider claim is
the one a reader can falsify in thirty seconds and would then have no reason to
trust the rest.

**And here the standard remedy would have been actively wrong, which is worth
separating from "unnecessary".** A `V2` alongside `V1` keeps the version
carrying the subject published and consumable for the length of the window.
The whole point of this decision is that the subject must not be on the wire;
dual-publish would re-arm the defect under a rule written to protect consumers
that do not exist. The version bump is not a cost this ADR declined to pay — it
is a step that would have undone the change.

**Payments cannot honour a single command until it has the projection.** The
subscription is a precondition, not an enrichment, and the service's first PR
owes the record before it owes a charge. §3.2's Consumes cell is where that is
now written down. It is also the moment the exception above expires: from that
PR on, `AuthorisePayment` is a live contract and §9.2 binds it with no
exception.

**A command can arrive before the record it resolves against.** §9.4 orders
nothing between two deliveries, so an `AuthorisePayment` can overtake the
`OrderPlaced` it needs — the shape §3.2 already records for `ReleaseStock` and
`ReserveStock`. **A missing record is a wait, not a decline**: Payments must
not publish `PaymentDeclined`, which is a business verdict about a payer it has
not identified.

**The wait needs a mechanism that lasts as long as the wait, which the ordinary
retry envelope does not.** An earlier draft of this ADR said to fault the
command and let retries carry it, and §9.8's command policy is five exponential
in-memory attempts capped at a minute — so a reorder outlasting that reaches
the error queue §13.6 pages on, making an operational fault out of a race this
ADR calls routine, roughly fourteen minutes before the timeout that was
supposed to bound it. **Payments' command endpoint takes delayed
redelivery** — [ADR-021](ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)'s
delayed exchange is already on this broker — with a window reaching §9.6's
fifteen-minute payment timeout, so an order whose `OrderPlaced` never arrives
compensates on that timeout rather than paging well before it.

**This narrows the broker exposure and does not close it.** One shared
RabbitMQ principal still writes every queue
([#44](https://github.com/alexander-shamray/blueprint-backend/issues/44)),
so anyone reaching the bus can still send an `AuthorisePayment`. What they can
no longer do with **that command alone** is choose who it charges: a forged
command naming a real order re-triggers that order's own authorisation rather
than redirecting one at a customer of the sender's choosing.

> **The same principal can still select a payer, and an earlier draft of this
> ADR claimed otherwise while documenting the route two paragraphs below.**
> Forging an `OrderPlaced` seeds Payments' record, and a forged
> `AuthorisePayment` after it charges whoever that record names. So this is a
> narrowing, not a closure, and the honest statement of it is that **the
> command alone no longer carries the payer** — not that payer selection is
> gone.
>
> What the narrowing buys is cost and visibility rather than capability. The
> attack is now two messages on two exchanges instead of one, and the added
> one is an **event other services consume**: Ordering's own saga starts on
> `OrderPlaced` (§3.2), so a forged one runs a fulfilment saga for an order the
> write model has no row for, and Notifications sends a customer a message
> about an order they never placed. The single forged command left no such
> trace. What removes the capability is per-service broker identity
> ([#44](https://github.com/alexander-shamray/blueprint-backend/issues/44))
> or verifiable event provenance; nothing in this ADR does.

> **Nothing in this platform absorbs that duplicate today, and naming a control
> that does not reach it would be worse than naming none.** [§8.5](../08-caching-redis.md)'s
> `IdempotencyBehavior` is an Application-pipeline behaviour constrained to
> `IIdempotentCommand` and keyed on a `CommandId`; `AuthorisePayment` has
> neither, and as a `Common.Contracts` message it never enters that pipeline.
> [§9.5](../09-messaging.md)'s inbox is the broker-side control, and it keys on
> `(MessageId, Endpoint)` — a forger picks a fresh `MessageId`, so the inbox
> suppresses an accidental redelivery and not a deliberate second send. What
> would absorb it is **Payments treating authorisation as idempotent per
> order against its own `PaymentIntent`**, which is a rule that service's own
> PR owes and no chapter yet states. Until then the control is #44.

**A forged `OrderPlaced` could still seed a false record**, and that is the
same issue rather than a new one: it is a broader compromise, it is visible to
every consumer of that event rather than to none, and per-service broker
identity is what closes both.

**Dropping the saga's column takes two releases, not one.** §15.5 requires
every migration to be backward compatible with the release serving beside it,
and that release's saga writes `ordering.OrderFulfilmentStates.CustomerId` on
every `OrderPlaced`. So this release maps the column as a shadow property with
a conservative default — `NOT NULL DEFAULT '00000000-…'`, the one shape that
survives a roll-forward whose `INSERT` omits it *and* an old build
materialising a non-nullable `Guid` from rows the new build wrote — and the
`DROP COLUMN` is owed to a release where nothing writes it. The empty GUID is
nobody, where any other default would name a real subject that was never that
order's.

**Two releases are enough here and would not be with a live consumer, and the
difference is worth stating because the shorter sequence looks complete.**
§15.5's canary runs both releases at once over the same queues, so the ordinary
ladder — not merely a rollback — lets a new pod create the instance with the
column defaulted and an old pod take the next event for it, read `Guid.Empty`
and send its four-field `AuthorisePayment` naming nobody. That reaches no
decision today for the same reason the in-place contract change is allowed at
all: nothing consumes the command. A platform whose Payments is live needs
**three** releases — stop sending the field, drop the property, drop the
column — which is §7.4's own sequence with its *stop writing the old one* step
performed rather than skipped. Skipping a step is what having no consumer buys,
and it buys nothing once there is one.

**The rule is gated in two halves, and only together do they force a
decision.** `ContractTests` asserts that no command contract declares a member
spelled like a subject — a list of six spellings, and therefore a
**deny-list**, which passes every spelling nobody predicted: `OwnerId` reaches
Payments with that assertion green, measured rather than argued. So a second
test enumerates **every member the judged commands are approved to carry**, and
any name absent from it fails the build.

**The allow-list does not decide whether a new member is a subject; it makes
adding one impossible to do quietly.** The verdict is still a person's, and the
build going red is what puts it in front of them — the scaffold's rule, that a
tool refusing input it has never been shown beats one that guesses, and the fix
this repository already applied to a terminal-state check that listed what it
refused instead of what it accepted. Stated this way because the earlier
wording — *enforced rather than reviewed* — claimed a completeness a
substring list cannot have.

**Defining "command" is the part that had to be got right, because the obvious
definitions fail in opposite directions.** §9.1
states one implication only — commands do not implement `IIntegrationEvent` —
so the converse is not available:

- **Every non-event refuses what the rule allows.** It sweeps in the payload
  records events carry, and an event is *permitted* a subject: `OrderPlaced`
  holds the one this ADR requires it to keep. An event that factored that field
  into its line type would fail a build for doing something legal.
- **Non-events minus the event closure lets one through.** That was the fix for
  the first and it created a worse fault: a payload carried by *both* a command
  and an event became exempt because an event reached it, so a subject inside it
  would travel on the command unjudged — a false negative on the exact path this
  decision exists to close.

**Reachability from a command root settles both.** The judged set is the
commands plus everything they carry transitively, so a shared payload is judged
(a command reaches it) and a purely-event payload is not (none does).
`StockLine` is judged through `ReserveStock`, because a subject one level down
reaches the same decision as a top-level one. The consequence for a shared type
is worth stating: it may not carry a subject at all, because the command side
forbids what the event side permits, and the stricter rule is the direction a
gate must fail in.

Controls sit beside the rule, because an absence-assert cannot fail
informatively on its own: one points the detector at `OrderPlaced` and requires
it to find the `CustomerId` this ADR keeps; one names all seven command roots
§3.2's Accepts columns list, so discovery cannot quietly drop four of them; one
asserts the exemptions really are excluded; and one exercises **every declared
subject spelling** rather than the first, after review found five of the six
unobserved — a control carrying the coverage defect it exists to catch. Its
cases are **generated from the vocabulary**, because the fix's own first
attempt restated the vocabulary as a case list and a spelling added to
everything except that list arrived unobserved all over again.

A gate that quietly stops
covering its surface is this repository's most-repeated failure, and an empty
offender set reads the same whether the rule holds or the detector broke.

**The shared-payload case is pinned with synthetic contracts, because the real
ones cannot express it.** No live payload is carried by both a command and an
event, so every assertion over the contract assembly stays green under the
rejected definition — the defect could be measured by hand and not held closed.
Four probe types in the test assembly supply the shape and are driven through
the same closure, which is why that closure takes its type universe as an
argument rather than reading a field.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
