# ADR-178 — A listing order's index covers its page, and a cursor's page has its own statement (#1626)

**Decision.** The public listing's two indexes include every column of
`Products` that `GetProductsHandler` reads: `IX_Products_PublishedAt_Id`
includes `Name`, `ThumbnailUrl`, `PriceAmount`, `PriceCurrency` and
`WithdrawnAt`, and
`IX_Products_Name_Id` the same with `PublishedAt` for `Name`. The migration is
`CoverProductListingIndexes`, written as SQL because `IncludeProperties`
cannot name a complex type's columns, so the model snapshot does not record
the include lists. That is an exception to
[§7.4](../07-persistence.md)'s rule that a write-model table's DDL is
generated from its configuration, and §7.4's table states it. The handler
sends one statement for a first page and another for a cursor's page, in each
ordering, where it sent one per ordering with `@After… IS NULL OR` in front
of the keyset predicate.
`ProductListingPlanTests` holds both halves on the plan the real statements
compile to. This adds to
[ADR-073](ADR-073-the-product-listing-takes-a-search-and-a-closed-sort-and-its-cursor-carries-both.md),
whose walk from the cursor is what both halves restore.
**Why.** The stress run 20261030-190623 on e94c4dc65 read the listing as the
largest CPU statement at 2000 users: 14 331 runs, 12.7 s of CPU. Its plan in
Query Store scanned the clustered index, all 283 products, and sorted them
for every page. The keyed index held neither the name nor the price, so
walking it cost a lookup a row, and over a few hundred rows the planner
priced that above a scan and a sort; a plan compiled on 2026-10-26, over a
smaller catalogue, had walked the index. Nothing in the statement changed
between the two, only the table's size, so the shape would have flipped again
as the catalogue grew. With the columns included the walk needs no lookup and
wins at any size. The catch-all predicate was the second half: a predicate
that is true for every row when its parameter is null cannot be a seek, so a
cursor's page walked the index from its end and filtered up to the cursor,
reading every earlier page again. ADR-073 promises a walk from the cursor.
**Consequences.** Both indexes now carry the listing's columns, so a product
write that changes its name, price, thumbnail or withdrawal updates them as
well as the table; products are written rarely beside how often they are
listed. A schema change to an included column must rebuild the two indexes
without it first, as `PricingServiceTests` does around its collation change,
and since the snapshot does not know the include lists, an EF-generated
migration that touches these indexes would drop them silently: the plan test
is what fails then. The handler holds four statements where it held two.
The first page is not cached: whether the walk is cheap enough is for
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)'s
next two-machine run to say, and a cache still needs
[§8.2](../08-caching-redis.md)'s invalidation. `OPTION (RECOMPILE)` was
refused for the cursor's page: it compiles on every call of the busiest
read, where a second statement compiles once.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
