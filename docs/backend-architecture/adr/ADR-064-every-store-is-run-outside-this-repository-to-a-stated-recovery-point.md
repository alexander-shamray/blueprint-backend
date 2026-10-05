# ADR-064 — Every store is run outside this repository, to a stated recovery point

**Decision.** `deploy/helm` charts this platform's workloads and no store, so
every stateful store is run by the deployment, outside this repository: each
service's SQL Server database and the BFF's, both Redis instances, the broker,
Keycloak's database, and the log and trace stores. Of whoever runs each, this
repository demands three things before customer traffic: a **recovery point**
and a **recovery time** stated per store, a **role** that runs the restore, and
a restore that role has rehearsed. The figures are the adopter's
([ADR-062](ADR-062-the-domain-is-a-reference-implementation.md)); what each
store's loss costs is this platform's, and is stated so the figure is chosen
against it:

| Store | What a restore must bring back | Demanded |
|---|---|---|
| A service's database | The store of record: every order, payment, shipment and notification, the saga's instances, and the outbox rows not yet dispatched | A recovery point and a recovery time |
| Keycloak's database | Every customer's account and credential, which exist nowhere else; the realm gate checks the realm's token obligations ([ADR-042](ADR-042-the-deployed-realm-is-checked-at-deploy-time.md)) and no account | A recovery point and a recovery time |
| The broker | Messages accepted and not yet consumed, messages parked in `_error` and `_skipped`, and the saga's pending timeouts on the delayed exchange ([ADR-021](ADR-021-saga-timeouts-are-scheduled-by-the-broker.md)) | A recovery point: the disk under the node, kept across a restart |
| Both Redis instances | Nothing. The cache is never a store of record ([ADR-006](ADR-006-redis-for-cache-and-coordination-never-as-a-store-of-record.md)), and a command's durable half is its marker in the service's own database ([ADR-037](ADR-037-the-idempotency-marker-is-a-row-in-the-commands-own-transaction.md)) | Replaced empty, never restored: a restored coordination instance would bring back locks and claims for work that has since finished |
| The log and trace stores | Nothing a restore owes | A lifetime ([§13.4](../13-observability.md)), not a recovery point |

**The broker's queues are classic and durable, and every publish is
confirmed — MassTransit's defaults, kept.** Read from a running broker, not
from memory: MassTransit declares each receive endpoint's queue with
`x-queue-type: classic` and `durable: true`, and opens every channel in
confirm mode. No queue is replicated, and no code or policy here says
otherwise. That is kept because the outbox ([§9.4](../09-messaging.md)) holds
an event until the broker confirms it, so what a broker can lose is what it
had accepted and not yet delivered — and a replicated queue would not save the
saga's timeouts, which the delayed-exchange plugin keeps on the node that
received them whatever the queue type.
**Why.** Four documents leaned on a backup and none stated one: ADR-021 and
[§9.6](../09-messaging.md) count "one database to back up" as a cost, ADR-009
counts Keycloak as one more thing to back up, and `migration-failure.md` asked
for a restored copy of a database nobody had said was backed up. A store nobody
is named to restore is restored by nobody, and the cost differs so much between
stores — the store of record against a cache that must not be restored at
all — that one sentence for every store would be wrong for most of them.
**Consequences.** A deployment that cannot state these figures and name the
role is not ready for customer traffic, and nothing in this repository can
check that it has. A broker node's loss loses the messages and pending
timeouts it held, and a saga whose timeout is lost is found only by §13.6's
saga-age condition, whose signal is still owed. Making the queues replicated
is a code change and not a broker policy, because the queue type MassTransit
declares wins over a virtual host's default and a policy cannot change a
queue's type; it is the next record's to take, when a deployment demands the
broker survive a node. And
`migration-failure.md`'s step that verifies against a restored copy can be run
only where the deployment has a backup to restore from.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
