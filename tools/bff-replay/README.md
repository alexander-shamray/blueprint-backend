# bff-replay

ADR-051's rebuild for the BFF's order projection. It sends the eight events
the projection reads to the BFF's queue, from the only place a delivered
event still exists: its publisher's outbox.

## What it can restore, and what it cannot

**A replay reaches back one outbox window and no further.** RabbitMQ keeps
nothing a consumer has acknowledged, so the last copy of a delivered event is
its publisher's processed outbox row, kept for `RetentionPolicy.OutboxWindow`
([§9.4](../../docs/backend-architecture/09-messaging.md)). An order whose
every event is older than that window, which every service registers as one
`new RetentionPolicy()`, is not restored by any run of this tool: after
`--reset` it is simply absent from the buyer's history. Recovering that is the BFF database's backup, not this.

So, in order of preference:

1. **Repair** — run with no argument. Every row in the window is sent; the
   BFF's inbox drops each one it has already handled, and the rest fill what
   the projection never received. Nothing is deleted. This is the answer to
   a gap: an order stuck as unattributed, a missing despatch.
2. **Rebuild** — run with `--reset`. The projection's order rows, their lines
   and the queue's inbox rows are deleted in one transaction, then the window
   is replayed. This is the answer to a projection that is *wrong*, not one
   that is incomplete, and it costs every order older than the window.
3. **Restore** — when what is wrong is older than the window, restore the
   BFF's database instead.

**`--reset` keeps `bff.Products`.** A product's name is published once,
usually long before any outbox window, so deleting the names would blank
every product Catalog's window no longer holds. When the names themselves are
wrong, delete from `bff.Products` by hand before the run, knowing that only
products Catalog has published within its window come back.

## What it needs

Six connections, read from its environment, and it refuses to start with any
of them missing — every missing key named in one message:

| Key | What it is |
|---|---|
| `ConnectionStrings__Bff` | The BFF's runtime login ([§7.1](../../docs/backend-architecture/07-persistence.md)), which may delete the projection's rows |
| `ConnectionStrings__RabbitMq` | The BFF's own broker account, `bff-svc` — in a cluster, the value of the Secret the BFF's chart names for its broker |
| `ConnectionStrings__CatalogOutbox` | A login that can read `catalog.OutboxMessages` |
| `ConnectionStrings__OrderingOutbox` | A login that can read `ordering.OutboxMessages` |
| `ConnectionStrings__PaymentsOutbox` | A login that can read `payments.OutboxMessages` |
| `ConnectionStrings__ShippingOutbox` | A login that can read `shipping.OutboxMessages` |

**The broker account is the BFF's on purpose.** `bff-svc`'s grant writes the
BFF's own `bff-` exchanges and no `Common.Contracts` exchange
([ADR-036](../../docs/backend-architecture/adr/ADR-036-the-broker-has-a-per-service-identity.md)),
so the one mistake that would hurt — publishing a replayed event to its
contract's exchange, which redelivers it to every other consumer — is refused
by the broker, whatever the tool does. Do not run it under a wider account.

**The publishers' logins should read and nothing else.** The tool only ever
selects from the four outbox tables, but a service's own runtime login can
write them; mint a login with `SELECT` on the one table for the run, and drop
it afterwards.

Against the local Compose stack, the values are the ones
`deploy/compose/services/web-bff.yml` and each service's own unit give their
hosts, with the container names replaced by `localhost` and the ports
Compose publishes.

## Running it

From the repository root, with the six keys set:

```bash
dotnet run --project tools/bff-replay            # repair
dotnet run --project tools/bff-replay -- --reset # rebuild
```

The BFF must be running for a repair: the tool only sends, and the BFF's own
consumers apply what it sends, through the same handlers and the same inbox as
live traffic. Live events arriving during a repair are harmless — the
projection ranks facts and never overwrites one
([§10.7](../../docs/backend-architecture/10-api-gateway.md)) — so a repair
needs no maintenance window.

**`--reset` needs the BFF's consumption stopped.** Scale the `web-bff`
deployment to zero (or stop its Compose service) before the run, because the
handler's write and the inbox row commit separately
([§9.5](../../docs/backend-architecture/09-messaging.md)), so a message
handled just before the reset whose inbox row lands just after it loses its
facts for good: the replay is then dropped as a duplicate.

The BFF is the queue's only consumer, so the replayed events wait on
`bff-order-events` until it is back. When the run has finished, resume the
BFF, then watch the queue's depth fall to zero — the broker's own view of
`bff-order-events`, the depth `QueueBacklogGrowing` alerts on — and trust
the projection only once it has.

Before it deletes or sends anything it opens the BFF's database and all
four outboxes and waits for the broker to answer, so an unreachable
connection stops a repair or a `--reset` with the projection untouched.

## Reading what it prints

One line per publisher as it finishes — how many events it sent, and the
`OccurredAt` of the oldest. **That oldest instant is the window the rebuild
actually reached**: an order placed before the oldest of the four lines is
outside it. Then a count per event type, and the total.

| Exit | Meaning |
|---|---|
| 0 | The window was sent |
| 2 | Refused before anything was opened: a bad argument or a missing key |
| any other | The run failed part-way, and the lines above say which publishers finished. A repair can be run again as it is. For a `--reset`, look for the `Reset:` line: without it nothing was deleted, so fix the cause and run again; with it the projection is partial, and `--reset` run again completes it, unless the same failure repeats, in which case restore the BFF's database instead |

## What it does not do

- It never writes to a publisher's database and never sends to a
  contract's exchange.
- It sends no unprocessed row: those are still the publisher's dispatcher's,
  and reach the queue that way.
- It sends nothing outside ADR-051's eight, whatever else the outboxes hold.
- It is not built into an image. It runs where an operator can reach the six
  connections, and its suite is the BFF's, because the proof that it works is
  the BFF's own consumers rebuilding the projection they built the first time.
