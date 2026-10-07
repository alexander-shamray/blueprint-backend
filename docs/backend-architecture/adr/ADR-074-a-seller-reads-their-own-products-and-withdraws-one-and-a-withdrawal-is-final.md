# ADR-074 — A seller reads their own products and withdraws one, and a withdrawal is final

**Decision.** `Product` records its `Seller`, the subject of the request that
published it, and never a body field ([§11.4](../11-identity-authorization.md)).
A product's writes are its seller's: `ProductOwnership.IsCallers` gates
`ChangePriceHandler` and `WithdrawProductHandler`, and any other caller gets
`ProductErrors.NotFound`, as a caller cancelling another customer's order
does. `POST /v1/catalog/products/{id}/withdrawal` is keyed under
[ADR-058](ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md)
and stamps `WithdrawnAt` through `Product.Withdraw`, whose
`ProductDiscontinuedDomainEvent` Catalog's mapper publishes as
`ProductDiscontinued` ([§9.3](../09-messaging.md)). A withdrawal is final:
nothing undoes one, and both a second withdrawal and a price change after
one are `ProductErrors.Withdrawn`, a rule failure
([§10.5](../10-api-gateway.md)). Both public reads hide a withdrawn product, so
the listing leaves it out and the one-product read answers it as an unknown
id ([§6.5](../06-cqrs.md)), and the pricing hop leaves it out of a quote as it
does an unknown id ([§9.7](../09-messaging.md)).
`GET /v1/catalog/products/mine` lists the caller's own products, withdrawn
ones included with their `WithdrawnAt`, newest first
over a `(SellerId, PublishedAt, Id)` seek, behind `catalog:write` and the
gateway's `catalog-own` route ([§10.2](../10-api-gateway.md)). No new
permission: whoever publishes holds `catalog:write` already.
**Why.** A seller could publish and could neither see what they had published
as a set nor take a product down, and the `ProductDiscontinued` that
Ordering's projection consumes had no publisher. Final rather than
reversible, because Ordering's projection re-lists a product on any price
newer than its withdrawal watermark ([§6.6](../06-cqrs.md)): a price change
after a withdrawal would put the product back on sale with no one having
decided to. Refusing it in Catalog keeps that one rule true at its source.
Ownership arrives with withdrawal because a withdrawal anyone holding
`catalog:write` could make on anyone's product is a hole, and the price change
had the same one. A 404 rather than a 403, because a 403 confirms the
product exists. A literal gateway route rather than the catch-all, because
the own list names a caller and `catalog-public` is anonymous at the edge.
**Consequences.** Nothing records who published a product before this
column, and the seeder publishes as nobody, so those rows keep a null seller,
belong to no caller, and cannot be repriced or withdrawn through the API
until an operator sets their `SellerId` in SQL, after which that seller acts
through the API. A direct update of the price or of `WithdrawnAt` would
stage no event, and Ordering's projection would go on selling the product at
its old price. A seller who withdraws by mistake publishes again under a new
id, and orders already placed keep the old one, whose name the BFF's
projection still holds. The BFF consumes no
`ProductDiscontinued`: a withdrawn product's name stays readable on the
orders that bought it, which is the reason to keep it. A deep link to a
withdrawn product now answers 404 where it answered the product, including
for a buyer who ordered it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
