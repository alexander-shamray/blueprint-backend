# ADR-185 — Every service database runs read-committed snapshot, and its migrator refuses one that does not (#1653); supersedes ADR-112's locking reads

**Decision.** Every service database runs with `READ_COMMITTED_SNAPSHOT ON`
([§7.1](../07-persistence.md)). EF Core's SQL Server provider sets it in the
`CREATE DATABASE` a migrator runs when it meets no database, on every engine
but Azure SQL Database, which has it on already; §7.1's provisioning script
sets it on a database an operator makes beforehand. Each service's
`MigrationRunner` reads it before any DDL, through the `MigratorCommand`
beside its `DbContext` and
`Common.Infrastructure.Migration.ReadCommittedSnapshot`, which reads
`is_read_committed_snapshot_on` from `sys.databases`; a database
with it off fails the job with a sentence naming the statement, having
migrated nothing ([§7.4](../07-persistence.md)). A database not yet made is
not read, since the provider's `CREATE DATABASE` is what makes it.
`ReadCommittedSnapshotTests` reads three real databases, one made by a plain
`CREATE DATABASE`, one switched on by hand and one the provider made, and
`MigratorIsolationTests` holds every `*.Migrator` under `src/` to the check
before its `MigrateAsync`.
**Why.** [ADR-112](ADR-112-the-sagas-outbox-transaction-is-readcommitted-and-only-the-sagas-row-lock-is-serializable-992-supersedes-adr-032s-serializable-outbox-transaction.md)
says the Ordering database does not run read-committed snapshot.
[#1653](https://github.com/alexander-shamray/blueprint-backend/issues/1653)
found every database on the stress stack running it, all eight made by the
migrators, and a database made by hand on the same server running read
committed without row versioning: the provider's `CREATE DATABASE` appends
`ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON` when
`SERVERPROPERTY('EngineEdition')` is not 5.
No file in the tree set it, so two deployments of the same release could
differ by who made the database. Each service suite's fixture has the
service's migrator make its database, so those suites have run under row
versioning, and
so has every stress run, the knees of
[ADR-172](ADR-172-the-stress-runs-generator-may-run-on-a-second-machine-1589-adding-to-adr-170.md)
included. Turning it off was refused for that reason: every measurement
would then describe a system nobody runs, and each service's unhinted reads
would wait on its writers again. The lock arguments that keep data right do
not depend on the choice, because each takes its lock by a hint, a level or
an application lock: [§6.6](../06-cqrs.md)'s `HOLDLOCK`,
[ADR-137](ADR-137-a-placement-and-an-erasure-for-one-customer-take-one-application-lock-and-run-one-after-the-other-1191-amending-adr-099.md)'s
application lock and `REPEATABLEREAD`, [§9.4](../09-messaging.md)'s
`UPDLOCK, READPAST` claim and [§9.6](../09-messaging.md)'s serializable
saga load. Read-committed snapshot changes only a read at read committed
that names none of these, which reads the last committed version instead of
waiting for a writer. Having the migrator set it was refused too: §7.1's
migrator holds `db_ddladmin` and `db_datawriter`, not `ALTER` on the
database, and the switch waits for every other connection to leave, which
on a live database is a deploy waiting on the release it replaces. A
migration would hold neither the permission nor the guarantee, because it
runs once, and a database made after it never meets it.
**Consequences.**

- **ADR-112's delivery read no longer waits.** Its first consequence said a
  cold plan made the outbox's delivery read wait on other consumers'
  uncommitted rows; under row versioning the read takes the committed
  version and waits for no writer. The `DELETE` beside it still does, since
  a statement that modifies rows takes update locks at any setting, which is
  the wait
  [ADR-167](ADR-167-the-saga-outbox-is-clustered-on-its-inbox-message-index-1570-adding-to-adr-032-and-adr-112.md)
  measured.
- **Row versioning costs tempdb.** An update or delete keeps the row's
  previous version in tempdb's version store until no statement that began
  before it still reads, and each row it versions carries 14 bytes more. A
  long-running read holds versions for as long as it runs, so tempdb's size
  is a capacity question for every service database, not only Ordering's.
- **A first deploy onto a database made without it fails, on purpose.** The
  remedy is one statement and a rerun. A database switched off after it was
  made is caught at the next deploy, not before: the check is the
  migrator's, and no running service reads it.
- **The refusal reaches the operator through the runner's failure
  sentence**, which says the schema may be partially applied. The refusal's
  own text, logged with it, says nothing was migrated.
- **The provider's behaviour is now a pinned dependency.** A provider
  release that stops setting it fails `ReadCommittedSnapshotTests`, rather
  than leaving each new database without row versioning unremarked.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
