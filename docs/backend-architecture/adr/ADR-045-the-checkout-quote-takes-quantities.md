# ADR-045 — The checkout quote takes quantities

**Decision.** `/bff/v1/checkout/quote` takes a request body of quantified
lines — `currency`, and `lines` of `{ productId, quantity }` — instead of a
repeated `productId` query parameter, and is therefore a `POST` rather than a
`GET`. `QuoteLine` gains `Quantity`, echoed from the request, and `LineTotal`,
computed from it; `QuoteResponse.Total` becomes the sum of the line totals,
which is the basket. Two lines naming the same product are **merged**, as
`Order.AddLine` already merges them one service over, and the bounds are
checked against the merged quantity on both sides. The bounds —
`OrderLimits.MinQuantity`, `MaxQuantity` and `MaxLines` — move into
`Common.Contracts.Ordering.V1`, and `PlaceOrderValidator` and the BFF's new
`QuoteRequestValidator` both read them rather than holding literals. `v1`
changes in place; there is no `/v2`.

**Why.** `QuoteResponse`'s own summary states the purpose the total exists to
serve — "the total is computed here because every client would otherwise
compute it, and two clients computing a total is two places to get rounding
wrong" — and the implementation could not satisfy it, because the request
carried nothing to multiply by. `Total` was the sum of **one unit of each
distinct product**: for two of A and one of B it answered `A + B`, not
`2A + B`. That is correct against the old contract and useless to the screen
the contract exists to serve, and a client holding a cart had exactly two
options, both of which defeat the rule. Multiply client-side — which is
precisely the second place computing money that the design forbids, and the
one with the worst floating-point story. Or show no basket total at all, which
is what the reference frontend shipped, relabelling the figure "quoted unit
prices". **The rule was right and the endpoint could not honour it**, so the
next client to want a basket total would either recompute money or ask the
same question again.

Given quantities, four smaller decisions follow and each had a cheaper
alternative worth refusing out loud.

**The body, rather than a parallel `quantity` array on the existing `GET`.**
ASP.NET binds `Guid[] productId` and `int[] quantity` independently: nothing
enforces equal length, and nothing enforces that the *n*th quantity belongs to
the *n*th id. A proxy that drops or reorders one repeated parameter silently
shifts every quantity onto the wrong product, and the only symptom is a wrong
total — the exact failure class this endpoint already grew a `HashSet<Guid>
outstanding` to catch on the **reply** side. Having paid for that lesson once,
it should not be reintroduced on the request side, and positional pairing
across two independently bound collections is a contract that cannot be
validated after the fact: at equal length there is no way to tell a correct
pairing from a shifted one. A composite `productId=<guid>:<qty>` was refused
for a different reason — it re-implements parsing inside a query parameter,
and the 400 it produces cannot be keyed to a field.

**The verb is the cost, and it is small here.** A quote is a read and `POST`
makes it look like a write. Nothing caches this: the group carries
`RequireAuthorization()`, emits no cache headers, and a quote is keyed to one
customer's basket, so a shared cache's hit rate on it is approximately zero.
And `GET` had a ceiling nobody chose — a basket at Catalog's
`GetPricesValidator.MaxProductIds`, spelled as repeated query parameters, runs
to several kilobytes of URL: inside every common limit, but not far inside, and
the margin is nobody's decision.

**`LineTotal` as well as `Total`, because otherwise the fix is half a fix.**
A cart shows "2 × £12.50 = £25.00". Supplying the first two figures and not
the third leaves every *line* total computed in the client, which is the same
defect one level down. `Amount` keeps its name and its meaning as the unit
price, because the screen needs it.

**A repeated product is merged rather than refused or dropped.** Dropping is
the one option with no defence: under the old contract a repeated id carried no
information, so `Distinct()` lost nothing, but a repeated *line* carries a
quantity, and dropping it discards part of the customer's basket.

Refusing is the tempting middle option, on the argument that summing silently
repairs a caller that has lost track of its own state. Two things rule it out.
`PlaceOrderHandler` calls a repeated product legitimate in as many words — "a
caller may legitimately send the same product twice — `Order.AddLine` merges
those into one line" — and `OrderTests.Place_merges_two_lines_for_the_same_product`
pins the merge in the domain, so refusing in the host that quotes *for* that
order is a **second duplicate policy**: a quote that refuses a basket the order
accepts, which is this record's own defect pointing the other way. And the
merge is not silent, which is the objection's real force: the reply echoes
`Quantity` per line, so a caller that sent two lines for one product gets back
one line carrying their sum and can see what became of it.

**The quantity bound is on the merged quantity, not on a line.** Because a
repeated product merges, a rule checked per line is not a bound on the order at
all — two lines at `MaxQuantity` place an order for twice it. Both validators
group by product and compare the sum, and each side pins it with a test,
including the acceptance case for two *distinct* products at the ceiling:
without that one, a basket-wide sum satisfies every boundary test while
refusing a legitimate basket.

**The bounds are Ordering's, read rather than copied.** A quote that accepts
what the order will refuse hands the customer a price for a basket they cannot
buy, and moves the refusal from the cart screen — where the quantity they typed
is still in front of them — to the checkout screen, where it is not. This
repository is explicitly hostile to a number that can drift from the one
actually enforced, so the alternative of two commented literals was refused:
`Common.Contracts` is the one assembly [§4.3](../04-solution-structure.md)
permits to cross a service boundary, and it is the only place both
`Ordering.Application` and `Web.Bff` may look.

**This is a new responsibility for the BFF, and that is the point.** The
endpoint declines to own the *product-count* ceiling, deliberately and
correctly — Catalog's `GetPricesValidator` owns that number, and a second copy
here would drift from the one actually enforced. That reasoning does not
transfer to quantity. Catalog prices a *product*; it has no opinion about how
many of one a basket holds and no validator that could own the bound. Quantity
is a basket concept, the BFF is the only host that sees a basket, and Ordering
is the only other holder of the same rule.

**This amends [§4.3](../04-solution-structure.md), and that is the part of this
record that outlives the endpoint.** That section says `Common.Contracts` holds
"integration event records and nothing else. No behaviour, no validation, no
domain types", and `OrderLimits` is none of those things and not an event
record either — so placing it there is a **prohibition gaining an exception**
([`docs/change-locality.md`](../../change-locality.md)'s own name for a Class C
change) rather than a placement decision. The prohibition's purpose is the
shared-kernel trap §4.3 describes: two contexts sharing an entity class cannot
evolve separately, so they deploy together, so they are one service with extra
steps. Three integers bounding a request's shape carry none of that, and the
exception is written to be unusable as a precedent for anything that does — the
test is that **both** sides are obliged by the value, so a copy could drift.
Catalog's `MaxProductIds` is the case that fails it: the BFF is not obliged by
it, defers to it, and a copy here would drift from the one actually enforced.

**Common.Application was the alternative and is the one worth naming.** §4.3
already calls it legitimate shared mechanism "used by six services and the
BFF", both assemblies already reference it, and the constant would have needed
no new project reference and no exception to any rule. It was refused because
`Common.*` is service-agnostic by construction and this bound is Ordering's:
putting it there makes a building block know about one service's domain, which
is a worse edge than the one this takes. `Common.Contracts` is already
organised per service — `Ordering/V1`, `Catalog/V1`, `Inventory/V1` — because
it holds cross-boundary facts about each, and this is one.

**Consequences.**

- **The request change is breaking and the reply change is additive**, which
  is why `v1` moves in place. There is one known consumer, it lives in a
  repository we control, and it lands in the same window; a `/v2` whose `/v1`
  has no remaining callers is a second code path maintained to serve nobody.
  The cost is real and stated rather than hidden: **this repository is a
  reference implementation, and "how do you version a BFF contract" is a
  question its readers will have, which this decision declines to answer by
  example.** An unknown consumer of the old shape gets a 405, not a
  deprecation window.
- **The consumer can no longer drive one interaction of its own contract.**
  `PricingContract`'s refusal — a basket past Catalog's id ceiling — used to
  reach the caller as a 400 through `UpstreamExceptionHandler`'s
  `InvalidArgument` arm, because the endpoint held no ceiling. It now fails
  `QuoteRequestValidator` before the hop, so Catalog is never asked. The
  interaction stays in the contract, because Catalog still **owes** the
  refusal and `PricingContractVerificationTests` is what holds it to that; the
  day the two ceilings part, that expectation is what says which way. The
  `InvalidArgument` mapping keeps its coverage in `QuoteEndpointTests`, which
  stubs the status directly rather than provoking it with a size.
  [ADR-023](ADR-023-the-consumer-driven-contract-is-a-linked-file-not-pact.md)
  is untouched: the mechanism is unchanged, and only which side drives one
  entry has moved.
- **`OrderLimits.MaxLines` and `GetPricesValidator.MaxProductIds` are two
  bounds that agree today and are not one fact.** The first is the most lines
  an order may carry, and the quote inherits it because it quotes for that
  order; the second is how many ids one price query may name. Nothing forces
  them to stay equal, and neither may be cited as the reason for the other.
- **`Web.Bff` gains a second project reference.** It exists for `OrderLimits`
  and nothing else. A reference existing is not permission to reach across
  [§4.2](../04-solution-structure.md) through it: the BFF consumes no Ordering
  message type and must not start.
- **`PlaceOrderValidator.MaxItems` is gone**, replaced by
  `OrderLimits.MaxLines`. Its quantity rule moves from `GreaterThan(0)` to
  `GreaterThanOrEqualTo(OrderLimits.MinQuantity)`, which is the same bound
  phrased against the constant rather than against the value below it. The
  error message changes with it.
- **`PlaceOrderValidator` gains a rule it did not have**, and it is a
  behaviour change to Ordering rather than to the BFF: an order whose repeated
  lines sum past `OrderLimits.MaxQuantity` for one product is now a 400 where
  it was accepted. Nothing asserted the old behaviour, and it was never
  intended — the ceiling has always been stated as a bound on how many of a
  product a customer may buy. It is named here because a reader looking for
  why an order started being refused will not think to look in a record about
  the checkout quote.
- **Two open questions are left for a later record rather than answered here.**
  `Amount` would read better as `UnitPrice` now that it sits beside a
  `LineTotal`, and was not renamed because that is a breaking change to a
  field every consumer reads, for clarity alone. And the local-development
  affordance in [`deploy/compose/README.md`](../../../deploy/compose/README.md)
  is now a `POST` with a body, which is no longer something a reader can paste
  into a browser.

> **The `Web.Bff` consequence above is superseded by
> [ADR-051](ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md),
> and nothing here has been edited.** An ADR is superseded and never
> rewritten: the decision this record took — the quote takes quantities,
> and `v1` changes in place — is untouched and still binding, and so is
> the reason `OrderLimits` crosses at all.
>
> **What moved is the prohibition, not the boundary it protects.** The
> BFF now consumes three of Ordering's published integration contracts,
> because ADR-051 serves the buyer's order read from a projection rather
> than from a fan-out. Reaching across §4.2 through a project reference
> is still refused; a published contract is the supported way across, and
> the reference itself still carries `OrderLimits` and nothing else.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
