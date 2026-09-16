# ADR-027 — The order summary stores product ids and resolves the name locally

**Decision.** `ordering.OrderSummaries.Products` holds a JSON array of product
ids and nothing else. Catalog's display facts — the name and the thumbnail —
are projected once per product into a new `ordering.Products` table, and
[§6.6](../06-cqrs.md)'s history query resolves them in a second, page-bounded
statement. The `ProductsUpdatedAt` column and the `JSON_MODIFY` patch handler
that versioned it are both removed.

**Why.** The design this replaces delivered its own payload only by accident.
It inserted `name` and `thumb` as empty strings and left them for "a later
`ProductPublished`" to patch in, and ordinarily none arrives: a product must be
published before it can be ordered, because `PlaceOrder` reads
`ordering.ProductPrices` and the same event fills it. So `ProductPublished` is
ordinarily consumed *before* the summary row exists, and a patch scoped to
summaries that already contain the product then touches nothing. The ordering
is the ordinary flow and not a guarantee: `IntegrationEventConsumer` runs the
two handlers sequentially but each commits on its own connection, so an order
placed in the window after the price handler commits and before the patch
handler runs would find its summary row patched. Narrow, and it is why this
reads *in the ordinary flow* rather than *always*. **Every summary carried
empty names in the normal flow** — which is the payload §6.6 exists to deliver,
filed as
[#121](https://github.com/alexander-shamray/blueprint-backend/issues/121).

The obvious repair — read the names at insert time instead — closes that door
and leaves a second one open. `ProductPriceProjection`'s upsert inserts on its
`NOT MATCHED` branch for `PriceChanged` as well, so a product whose
`ProductPublished` never reached the queue still acquires a price row and is
orderable. §6.6's rebuild callout describes that population, and this change
corrects it in passing: it read *unorderable until somebody republishes it*,
which the `PriceChanged` branch has never made true. An order placed through
that door would carry
an empty name permanently, and the only thing that could ever fix it is the
patch handler this ADR removes.

**Resolving on read fixes both, because it makes the name a fact about a
product rather than a copy inside an order.** A `ProductPublished` that arrives
late — or for the first time, months later — fills every order that ever
referenced that product, retroactively and with no rebuild.

**The load-bearing sentence in the old design was false.** It justified
denormalising the name on the grounds that "joining at read time is not an
option — the products live in Catalog". They do not, once Ordering projects
them: a primary-key lookup against a table in the same database is not the
cross-service join the argument was about. `ordering.ProductPrices` had
already been exactly that local projection of Catalog's data on the write path
since PR-20, on a table PR-18 shipped with its reader and no producer — so the
premise was false before this change was written.

**What that table held is the price, and it is worth being exact about it.**
`ordering.ProductPrices` has never had a `Name` column; the name was copied
once, into every order's JSON, and it is that copy this ADR removes. The
argument is that a local projection of Catalog was already established and
paid for, not that the name was stored twice.

**Consequences.**

A watermark now belongs to exactly one sequence. `ProductsUpdatedAt` versioned
as many `ProductPublished` streams as an order had products, so a delayed
rename of product B, handled after a newer one for product A, was discarded and
B kept a stale name — a residual §6.6 named and could not close without a
fourth JSON member compared inside an `OPENJSON` predicate. One row per product
retires it by construction rather than guarding it.

**The chapter's "one table, no joins" claim is narrowed, and it said so twice.**
The history query is now two statements. The second is one round trip
for the whole page rather than one per row, seeks a primary key, and carries
its ids as a single JSON parameter read through `OPENJSON` — against a patch
handler that scanned every summary in the table on every rename.

**It is deliberately not an expanded `IN` list, and the first draft of this
ADR said it was bounded by the page clamp.** The clamp bounds *rows* at a
hundred and each row admits a hundred items, so the distinct ids reach ten
thousand — and an `IN` list of that size exceeds SQL Server's 2,100-parameter
ceiling and fails the request outright. **A limit one multiplication away from
a stated bound is exactly the kind a reassurance hides**, which is why the
count is written out here rather than deferred to the clamp. What the trade
actually buys is a key lookup per distinct product against a handler that
scanned the whole table on every rename, and it is still the better half of
the exchange.

**The stored payload loses its member names, which removes a hazard rather
than moving it.** `SummaryProduct` was one type doing two jobs, and its three
JSON names were pinned with `[JsonPropertyName]` because the `JSON_MODIFY`
paths spelled them a third time in string literals no compiler reads. A `Guid`
array has no member name to mismatch, so `JsonSerializerOptions.Default`'s
case-sensitive binding — the quiet failure §6.6 spends a callout on — has
nothing left to fail on.

**Nothing in `src/` changes, and that is why this is cheap now and would not
have been later.** `OrderSummaryProjection` is unbuilt and Appendix C carries
no row that builds `OrderSummaries` at all, so there is no shipped code to
correct and no data to migrate. The same correction after that row lands is a
migration, a backfill and a projection rebuild.

**What this does not do is remove the second copy.** `ordering.Products` is
still a projection of Catalog's data into Ordering's database, with its own
staleness and its own rebuild procedure — §6.6's closing trap applies to it
exactly as written. What changes is that there is now one copy on the read path
instead of one per order line that mentions the product.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
