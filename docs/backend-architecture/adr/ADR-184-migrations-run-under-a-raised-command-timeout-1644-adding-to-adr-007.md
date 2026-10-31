# ADR-184 — Migrations run under a raised command timeout (#1644), adding to ADR-007

**Decision.** Every migrator's `MigratorHost` sets its context's command
timeout to `MigratorCommandTimeout.Seconds`, which `Common.Infrastructure`
owns and each service's `MigratorCommand` forwards, because §4.2 keeps a
migrator from referencing `Common.*` itself. The number is sized for a
command that rewrites a whole retained table, and kept small enough that one
blocked command on each of the Job's attempts, its `backoffLimit` and one,
fits under the age at which [§13.6](../13-observability.md)'s
`MigrationJobFailed` calls a Job stuck ([§7.4](../07-persistence.md)). The
runtime contexts keep EF Core's default.
**Why.** No migrator raised EF Core's default of 30 seconds a command, and
[ADR-180](ADR-180-the-outbox-key-is-random-and-its-claim-index-is-led-by-a-bucket-1623-supersedes-adr-161s-sequential-key-option.md)'s
`KeyOutboxOnRandomId` adds `Id` with a non-deterministic default, which SQL
Server writes into every row rather than recording as metadata, before the
clustered key sorts them again. On the Compose stack at `853dce80e`,
Ordering's outbox held 1 786 852 processed rows the retention purge had not
reached, and the migrator failed that command at 35 seconds; its transaction
rolled back and left the schema intact. The same migration, scripted and run
through sqlcmd, took 41 seconds. A retained table is at its largest on the
first deploy that rewrites it, so the migration that meets it is the one a
bench never sized. Bounded so, a command blocked on a lock fails with its
text in the Job's log, where `migration-failure.md` starts, and a Job blocked
on every attempt has failed before the stuck branch pages. A bound sized for
one attempt alone would let those retries outlast that age, and the alert
reads the Job's Failed condition rather than its pods so that retries are
not what pages.
**Consequences.** A migration command may now hold its locks for minutes
before it fails, where it failed in 30 seconds. The bound is a size, not a
guarantee: a rewrite that outruns `MigratorCommandTimeout.Seconds` fails as
before, and the remedy is then a smaller table at deploy time or a migration
split across releases, as §7.4 already asks of destructive changes. The
bound is per command, not per attempt: an attempt running several long
commands, as this migration's column rewrite and key rebuild are, can follow
blocked ones past the stuck age, and the stuck branch then pages on a deploy
that succeeds. The stuck age now has a second dependant beside the deploy's
`--timeout`: lowering it below the attempts times the bound breaks this
decision. Catalog's and Privacy's republish runs build the same context and
take the bound too.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
