# ADR-073 — The product listing takes a search and a closed sort, and its cursor carries both

**Decision.** `GET /v1/catalog/products` takes `q` and `sort` beside `cursor`
and `limit` ([§6.5](../06-cqrs.md)). `q` is a contains-match on `Name` by
`LIKE`, every `LIKE` metacharacter in it escaped so the caller's text is never
a pattern, trimmed, blank meaning no search, and no longer than
`GetProductsValidator.MaxSearchLength`. `sort` is `ProductSort`'s closed set,
an absent or empty one meaning `ProductSort.Newest`, and anything else a
field-keyed 400 ([§10.5](../10-api-gateway.md)). `ProductCursor` carries the
ordering and the search it was minted under beside the seek, and
`GetProductsValidator` refuses with a 400 on `Cursor` a readable cursor whose
pair differs from the request's. An unreadable cursor still reads as the
first page. Each ordering has its own keyset seek and index: `(PublishedAt,
Id)` as before, and `(Name, Id)` for the name order. The listing stays
uncached, and `q` changes nothing about that.
**Why.** A client could page the server's one order and could neither search
nor sort, and filtering the pages it holds would misstate what exists.
[§6.5](../06-cqrs.md)'s cursor encodes a position in one ordering, and once
there are two orderings and a filter a cursor read under another query is a
position in a sequence it does not belong to: it skips or repeats rows with no
error anywhere. Refusing it is the only answer
that cannot be wrong, and carrying the pair in the cursor is what lets the
server know. There is no price order because each product keeps its own
currency (`Product.ChangePrice`), so one numeric order across them would rank
an amount in one currency against an amount in another. `LIKE` rather than
full-text, because full-text needs a component the platform's SQL Server
image does not carry and a catalogue to be large before it pays for itself.
**Consequences.** A contains-match cannot seek: the read walks the
ordering's index from the cursor and stops after `limit + 1` matches, so a
search that matches often costs what a page costs, and one that matches
rarely reads the whole table before answering an empty page. That is
acceptable for a reference catalogue and is the first thing to replace when
one grows; the cursor rule survives the replacement. The search travels in the
cursor, so a cursor is as long as its search. A client that changes its query
and keeps its cursor gets a 400 where it used to get rows, which is the
point. [ADR-016](ADR-016-cursor-pagination-by-default.md) holds in full; what
narrows is §6.5's statement that the sort is not a public contract, which is
now said of a cursor's layout and no longer of this listing's order.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
