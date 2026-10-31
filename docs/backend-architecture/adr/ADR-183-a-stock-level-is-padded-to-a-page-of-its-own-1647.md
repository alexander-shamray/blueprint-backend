# ADR-183 — A stock level is padded to a page of its own (#1647)

**Decision.** `catalog.StockLevels` carries a `Padding` column, `char(4100)`,
that nothing reads or writes, so that no two rows fit on one 8 KB page and
each product's level sits on a page of its own. `StockLevelConfiguration` maps
it as a shadow property with an empty default, so `migrations add` emits it;
the migration `PadStockLevelsToAPage` adds it and rebuilds `PK_StockLevels`,
because adding a column with a constant default writes no row and the rows
already there would keep sharing their page until each was next updated.
`StockLevelsSchemaTests` holds it: with two rows or more, the table has at
least as many pages as rows.
**Why.** [ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)'s
run 20261031-102448 on 853dce80e read `PK_StockLevels` as its hottest index by
far: 168.2 s of page latch over 26 012 updates, against 44 s for the next. At
2800 users `StockLevelProjection`'s `MERGE` and the public listing were the
second and third statements of the run, and 35.9 s of the listing's 42.3 s
was buffer latch. The table's 130 rows sat on one page, so the projection's
32 writes a second and the listing's 166 reads a second, each seeking 21
levels, all latched that one page; with the host's CPU spent, SQL Server at
23% of 24 cores and none idle, a latch holder taken off its core held every
waiter behind it. Read-committed snapshot takes the listing's locks away and
not its latches; every database of that stack ran it, the migrators having
created them through EF Core's SQL Server provider, while a database created
by a plain `CREATE DATABASE` on the same server did not. On a
throwaway SQL Server held to 2 CPUs, with snapshot reads, 8 writers at about
55 a second over 24 products and 32 readers at about 280 a second, 30 s each:
one page gave 106 s to 266 s of latch and a 12 ms to 37 ms listing; three rows
a page, 45 pages, 13.5 s to 23.9 s and 5.4 ms to 8.1 ms; a page a row, 1.6 s
to 7.5 s and 0.9 ms to 1.4 ms. Writing only when the availability shown
changes, or a coarser value, was refused, because the listing shows the
quantity itself ([§6.5](../06-cqrs.md)); a memory-optimised table needs a
filegroup in every Catalog database and takes no `HOLDLOCK`; a cached listing
needs [§8.2](../08-caching-redis.md)'s invalidation, which
[E.3.3](../appendix-e-reference-domain.md#e33-8-caching) leaves the listing
without.
**Consequences.** A level costs a page, 8 KB, where it cost about 40 bytes:
the reference domain's few hundred products take a few megabytes, and a
million would take 8 GB. A listing page of 20 products now reads 21 pages of
the table, its look-ahead row's included, rather than one. In a catalogue
that large the writes spread over pages anyway, and the padding is all cost:
an adopter with one drops the column, and the schema test with it. The bench's 24 products and the stress
run's concentrate the writes more than a catalogue would; whether the stack
agrees is for ADR-172's next two-machine run to say. `Down` drops the column
and leaves each row a page of its own until the index is next rebuilt.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
