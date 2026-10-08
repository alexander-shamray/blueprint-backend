# ADR-087 — A caller cancels an order only as a customer request

**Decision.** `POST /v1/orders/{id}/cancel` accepts the reason
`customer_request` and refuses every other `CancellationReasons` code with a
400 keyed `Reason`, the response an unknown code already gets. The other four,
`out_of_stock`, `stock_timeout`, `payment_declined` and `payment_timeout`,
reach `CancelOrderCommand` only from `CancelOrderMapper`, on Ordering's own
command queue, where the workflow states them as facts. The parse is unchanged
and still shared with the message path; the refusal is the endpoint's alone.
This amends [§11.4](../11-identity-authorization.md)'s endpoint, and is the
correction for the paragraphs in [§9.6](../09-messaging.md) and
[§10.7](../10-api-gateway.md) that say the endpoint accepts all five codes.
**Why.** A reason from a caller is a claim nobody checks. With all five
accepted, a customer could cancel their own order as `payment_declined`. That
false reason was recorded on the order, and `OrderMetrics` counted it on
`orders.cancelled` under the wire code, which §13.3 tags by reason and not by
origin, so any customer could inflate the stock and payment series that a
dashboard reads as incidents. `Origin` already kept the buyer from being told
their card was refused, but it reached neither the order's recorded reason
nor the counter's tag. Refusing the codes at the one path a caller holds
fixes both at the source, where tagging the counter by origin would leave the
false reason on the order and put the fix in every query.
**Consequences.** A support agent holding `orders:admin` cancels through the
same endpoint, so a cancellation made by staff for a stock or payment reason
records `customer_request`; a reason staff may state is a new code, with its
own permission, not one of the workflow's. `Origin` stays, and the BFF's
origin-first map with it: an event recorded before this rule can pair a user
origin with a workflow code, and a payload with no origin can still arrive
from an error queue, so the reason alone still cannot say who cancelled. An
order a caller already cancelled with a workflow code keeps that reason. A
client that offered a caller the whole vocabulary now gets a 400 for every
code but `customer_request`.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
