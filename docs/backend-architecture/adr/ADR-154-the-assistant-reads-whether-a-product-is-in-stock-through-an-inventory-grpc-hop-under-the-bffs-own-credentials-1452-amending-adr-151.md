# ADR-154 — The assistant reads whether a product is in stock through an Inventory gRPC hop under the BFF's own credentials (#1452), amending ADR-151

**Decision.** Inventory serves a read that tells a customer whether a product
can be bought now, and the BFF's assistant calls it as the `get_stock` tool.
The answer is a flag and never a count: `stock.proto`, owned by
`Inventory.Api/Protos` and linked into `Web.Bff` as `pricing.proto` is, has one
RPC, `GetAvailability`, that takes product ids in their canonical text form,
at most a hundred, and answers an entry for each product Inventory holds a row
for, with `in_stock` true when the unreserved count is above zero. A product
with no row has no entry, because unknown and sold out are different facts to
tell a customer, and the tool says the first as `stock_not_known` and never as
out of stock. Inventory serves it on a second Kestrel endpoint, port 8081,
HTTP/2 only, for the reason Catalog's `appsettings.json` measures; the service
is authenticated and requires no permission, because the BFF's service account
carries none and the reply holds nothing a permission would guard. The BFF
reaches it under its own client credentials, never a customer's token, which
a turn does not hold ([ADR-151](ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-off-by-default-and-scoped-to-the-caller-1347-amending-adr-053-adr-149-and-adr-150.md)),
through `StockHop`, whose address and resilience budget are `PricingHop`'s
value for value, registered in the same order with the credential and
correlation handlers inside the resilience pipeline. The gateway routes
nothing to 8081 and its `inventory:admin` read of `/v1/inventory/stock` is
unchanged. The consumer-driven contract is `StockContract`, linked into
Inventory's suite as `PricingContract` is into Catalog's, and the sentence in
`AssistantRuntime.Instructions` that stock is unreadable is replaced by one
that says the tools tell whether a product is in stock and never how many.
**Why.** [ADR-151](ADR-151-the-web-bff-hosts-the-ai-assistant-for-signed-in-customers-off-by-default-and-scoped-to-the-caller-1347-amending-adr-053-adr-149-and-adr-150.md)
left stock out because nothing the BFF could reach answered it: the gateway
admits Inventory's reads only to `inventory:admin`, the BFF has no hop to
Inventory, and its projection holds no stock. Three ways to give it one were
open. Widening the gateway's route to any signed-in customer would publish the
count, which is the warehouse's number and not a customer's, and would give
the assistant a customer token it does not hold. Projecting stock into the
BFF's own schema from `StockLevelChanged` would add a table, a consumer, a
purge and a replay path to answer a question asked a handful of times a
conversation, and would still be a copy that is stale by a queue's lag; the
hop reads the row. A hop of the pricing shape is the one the platform already
operates, budgets, tests and fences, so the whole decision is a second
instance of it. The call is made inside a turn, which runs off the request
that started it, so it spends no hop of §9.7's budget and is on no request
path; were it on one, the depth would be one, `Client → Gateway → BFF →
Inventory`, and Inventory calls nobody. Net of what is reserved is the right flag, because
`StockItem.Available` already is: the count a stock-take sets is on hand
minus reserved, so above zero is what the next order could take.
**Consequences.** Inventory becomes the fourth service to serve gRPC and its
chart takes a second port and an ingress from `web-bff` on `grpc`, where it
admitted only the gateway; the BFF's network policy gains the egress, and the
smoke script's list of dialled Services reads `StockHop.cs`. The `bff` filter
in `ci.yml` names `Inventory.Api/Protos`, because the BFF image copies that
path and a filter that does not name it never builds the image. An in-stock
answer is a read and not a promise: another order may reserve the last unit
before this customer's, and the order path, which reserves by §7.3's statement,
is where that is decided, so the assistant must not promise a purchase from
it. An Inventory outage costs the stock answer only, as `inventory_unavailable`
the model reads and never a failure's own text; it is kept out of the BFF's
readiness, as Catalog's is. The release that ADR-153 adds for the assistant is
a second workload that dials Inventory and that Inventory must admit, so its
chart's egress and Inventory's ingress name it, which is the follow-up under
#1451 that lands with that chart; until then `web-bff` is the only admitted
caller and the assistant is off there. Appendix B registers nothing new,
because `Grpc.AspNetCore` is already one of its rows.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
