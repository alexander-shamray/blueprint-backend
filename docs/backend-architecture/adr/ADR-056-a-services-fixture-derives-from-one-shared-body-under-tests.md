# ADR-056 — A service's fixture derives from one shared body under tests/

**Decision.** What every service's `ServiceFixture` holds alike lives once, in
`tests/Common.TestSupport`: the SQL Server container and the service's own
database ([§7.1](../07-persistence.md)), the broker by either route — the
stock image with §14.1's configuration mapped onto it
([ADR-036](ADR-036-the-broker-has-a-per-service-identity.md)),
or §14.1's image built for a service that schedules
([ADR-021](ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)) — Redis
where the host claims keys on it ([§8.5](../08-caching-redis.md)), the
harness's grant on the service's broker account, the migrator's run and its
exit code ([§7.4](../07-persistence.md)), the reset, and the outbox, inbox,
marker and retention helpers ([§12.4](../12-test-strategy.md)). Each
`<Service>.TestSupport` keeps its name and §4.1's role, and its
`ServiceFixture` derives from the shared one, supplying what is the
service's own: its name, its migrator, its factory, the write its harness
needs, the stubs it starts and the helpers only its suites call.
`Common.TestSupport` references building blocks and no service.
**Why.** The copies differed in those parts, so a change to the rest
was an edit per service; the edits drifted apart in their comments and their
teardown order, and the scaffold copied Catalog's into each service it
rendered.
The shared body sits under `tests/` and not under `src/BuildingBlocks/`
because a building block is what hosts reference and what §4.2's
architecture gates bind: a test library there would put Testcontainers,
Respawn and xUnit in the tree the images build from. `tests/` already holds
libraries that are not test projects, so the name `*.TestSupport` means the
same thing for this one as for the rest. The per-service libraries keep
their name because §4.1's reason for them still holds — a service's suites
share its fixture and cannot reference each other — and each suite's
collection names that fixture's type, so renaming it would change every
suite.
**Consequences.** A fixture change is one file, and so is a fixture
regression: it reaches every service and every service the scaffold renders
at once. The shared class decides the shape of what a service may vary —
its constructor's arguments, a migrator, a factory, a grant and the stub
hooks — so a service that needs another container changes the shared body
rather than its own. Behaviour one service needed now holds for all: the
reset retries the deadlock a saga's commit can cause
([ADR-032](ADR-032-the-sagas-outbox-is-masstransits-in-the-sagas-own-transaction.md)).
Both broker routes sit in one file, so `deploy/compose/rabbitmq`'s
permission gate holds that file's stock route to the Dockerfile's `COPY`
lines even though the same file also builds the image. The project is named
like a building block and is not one.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
