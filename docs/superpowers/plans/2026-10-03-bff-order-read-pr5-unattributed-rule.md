# BFF order read PR-5 — the unattributed-order rule, its runbook and its panel — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put a watch on the one failure of ADR-051's projection that no
existing signal sees: an order the BFF holds payment or shipment facts for and
no buyer, which §10.7 returns to nobody. PR-2 published the gauge,
`bff.orders.unattributed`; this pull request loads the rule that reads it,
writes the runbook the rule names, gives §13.6 and §13.9 the rows
`deploy/observability/check.py` requires of every runbook, adds the row to the
runbooks index, and draws the gauge on the messaging dashboard.

**Architecture:** Five files, none of them code. The rule joins
`platform-alerts.yaml`'s `platform-messaging` group in the form the outbox
age rules already take: an age gauge, so no `for`; `max by (service_name)`,
because the gauge reads the database and every replica exports the same
number; a ticket rather than a page, because the order itself is untouched
and only one buyer's history lacks it. The runbook is
`docs/runbooks/unattributed-order.md`, written to the index's *Writing a new
one* rules: what the buyer sees, what is not affected, the lookalike (an
Ordering event that is late rather than lost), the queries with real table
and column names, the four places the missing event can be, and how to close
it — including deleting a row nothing will ever attribute. §13.6's first table
gains the alert's row and §13.9's table the runbook's, because check 9 fails a
runbook either table does not name. The panel goes on `outbox.json` under a
row of its own, unfiltered by the dashboard's `service` variable, which reads
a series the BFF never exports.

**Tech Stack:** Prometheus rule YAML, Grafana dashboard JSON (schema 39),
Markdown, Python 3.12 for the gates (`check.py`, the comment gate, the secret
scan, the locality gate).

**Spec:** `docs/superpowers/specs/2026-10-03-bff-order-read-design.md`,
sections 1 (*Monitoring*), 4 (PR-5's row and the order paragraph), 10 (the
gauge, its threshold argument and what the runbook looks for) and 12 (§13.6,
which this plan amends — see *Global Constraints*). The gauge, its meter, its
unit and its index are PR-2's plan, Task 7; the table and columns the runbook
queries are PR-1's plan; the rebuild tool the runbook sends a repair to is
PR-4's.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class D.** Touch set:

  `deploy/observability/alerts/platform-alerts.yaml`, `deploy/observability/dashboards/outbox.json`, `docs/runbooks/unattributed-order.md`, `docs/runbooks/README.md`, `docs/backend-architecture/13-observability.md`

  Why each, since the row is paths only: the rule file is where every loaded
  rule lives and the only one `check.py` reads as loaded; the dashboard holds
  delivery lag and read-model staleness already, and an order nobody can see
  is the same question one step on; the runbook is the procedure the rule
  names; the index is `docs/runbooks/README.md`'s table of every runbook and
  its alert; and chapter 13 holds the two tables `check.py` check 9 reads —
  §13.6's alert rows and §13.9's runbook rows. None of the five is on
  `docs/change-locality.md` §4's mutex list.
- **No `src/**`, no `tests/**`, no `deploy/helm/**`.** PR-1 and PR-2 carried
  the chart's halves (spec section 4); the gauge and its `AddMeter` line are
  PR-2's. This pull request reads names those plans published and writes none
  of its own outside the five files.
- **§13.6 and §13.9 move, and the spec said §13.6 would not.** Section 12's
  *places outside that table* says "§13.6 gains nothing in prose: the rule's
  alert table is the alert file's". `check.py`'s check 9
  (`deploy/observability/check.py`, the two `fail` calls in
  `check_chapter_inventories`) fails a runbook that no §13.6 row names bare in
  its Runbook column, and one §13.9's table does not name with its
  `docs/runbooks/` prefix. So the chapter gains one row in each table, Task 3
  writes both, and `/validate-blueprint` runs (spec section 12's last line).
- **The runbook is not shared.** One rule names it, so `SHARED_RUNBOOKS` in
  `check.py` gains nothing, and `check.py` is not edited at all: its
  `declared_instruments()` already scans `src/**/*.cs`, so PR-2's
  `CreateObservableGauge("bff.orders.unattributed", …, unit: "s", …)` in
  `src/BFF/Web.Bff/Observability/ProjectionMetrics.cs` exports
  `bff_orders_unattributed_seconds` as far as checks 4 and 6 are concerned, and
  `SOURCE_INPUTS` already covers `src`.
- **No alert on the gauge's absence.** PR-2's `ProjectionMetrics` contains a
  failed read into an absent series, which §13.6's containment callout argues
  is right for a transient outage and owes an absence alert for the permanent
  case, platform-wide. That debt is the chapter's and not this rule's; the
  runbook says what an empty panel means, and that is the whole of what this
  pull request owes it.
- **Depends on PR-4 having merged**, and therefore on PR-1 to PR-3. These are
  the names this plan consumes, as each plan spells them:
  - PR-1: database `Bff`, schema `bff`; `bff.Orders` with `OrderId`,
    `CustomerId NULL`, `AuthorisedAt`, `RefundedAt`, `DispatchedAt`,
    `DeliveredAt`, `TrackingNumber`, `FirstSeenAt`, `AsOf`; `bff.OrderLines`
    with the cascading `FK_OrderLines_Orders_OrderId`; `bff.InboxMessages`
    with `MessageId`, `Endpoint`, `HandledAt`.
  - PR-2: queue `bff-order-events` (`Web.Bff.Messaging.DependencyInjection
    .EventsQueue`), so its error queue is `bff-order-events_error`; meter
    `Web.Bff.Projection` (`ProjectionMetrics.MeterName`) with its `AddMeter`
    line in `Common.Web`'s `ObservabilityExtensions`; gauge
    `bff.orders.unattributed`, unit `s`, zero when every row has an owner,
    computed as the registered clock minus the oldest unowned `FirstSeenAt`
    and cached for a few seconds by `ProjectionStats`; the log message
    `Unattributed-order gauge read failed` when a read fails; the handlers
    inserting on a missing row (so a deleted unowned row comes back whole if
    its Ordering event ever arrives); unowned rows hold no lines, because
    lines arrive only with an Ordering event, which also brings the customer.
  - PR-4: the rebuild tool `tools/bff-replay` with a repair run (no
    `--reset`) that sends outbox rows to `bff-order-events` alone, and its
    procedure. **Its procedure's path is read from the merged tree in Task 2,
    Step 1**, because a runbook under `docs/runbooks/` with no alert pointing
    at it fails `check.py`'s checks 2 and 9; the plan expects PR-4 to have put
    the procedure in `tools/bff-replay/README.md` and says what to do if it
    did not.
- **The service name** the rule's `service_name` label carries is the host's
  `ApplicationName`, which `Common.Web`'s `ObservabilityExtensions` passes to
  `AddService` — `Web.Bff`, as `slo.js` reads `Ordering.Api` for Ordering.
- Prose wraps at 80 columns in British spelling; a YAML comment block is at
  most five lines (the comment gate's `BLOCK_LIMIT`), names no issue, PR or
  reviewer, and carries no emphasis.
- **Print no credential** anywhere, this plan included: the secret scan reads
  `docs/superpowers/`.

---

### Task 1: The rule, red against a runbook that does not exist yet

**Files:**
- Modify: `deploy/observability/alerts/platform-alerts.yaml`

`check.py` has no suite; it is its own test, and every step below runs it and
reads the failure it names. The order is the order the gate fails in: the rule
first, so the gate demands the runbook; the runbook, so it demands the
chapter's rows; the rows, and it passes.

- [ ] **Step 1: Confirm the base is green and the signal is published**

```bash
py -3.12 deploy/observability/check.py
grep -rn '"bff.orders.unattributed"' src/BFF/Web.Bff/Observability/ProjectionMetrics.cs
```

Expected: `observability gate: OK`, and one line naming the gauge. If the grep
finds nothing, PR-2 has not merged and this plan stops: check 4 would fail the
rule below for reading a series nothing exports, which is the gate doing its
job.

- [ ] **Step 2: Add the rule after `DeliveryLagHigh`, at the end of `platform-messaging`**

Insert this block after `DeliveryLagHigh`'s `runbook_url:` line (before the
blank line and `- name: platform-infrastructure`):

```yaml

      # An age, so no `for`, by the lanes' argument above: the wait is the
      # metric. `max` because the gauge reads the database and every replica
      # exports it. A ticket, because the order and its money are untouched and
      # only one buyer's history lacks it (§10.7); the runbook argues the
      # threshold, and §13.6's row states it.
      - alert: UnattributedOrders
        expr: max by (service_name) (bff_orders_unattributed_seconds) > 900
        labels:
          severity: ticket
          owner: service
        annotations:
          summary: "An order with no owner for over 15 min on {{ $labels.service_name }}"
          description: >-
            The BFF's order projection holds payment or shipment facts for an
            order that no Ordering event has attributed to a buyer, so neither
            order route returns it to anyone (§10.7). A payment or shipment
            event beating OrderPlaced is ordinary and resolves in seconds; this
            is past every cause that resolves itself or pages on its own.
          runbook_url: docs/runbooks/unattributed-order.md
```

`owner: service`, because §13.8 gives "projection lag … consumer failures" to
the team that owns the host, and the gateway — Platform's — is the edge, which
this is not.

- [ ] **Step 3: Run the gate and read the failure**

```bash
py -3.12 deploy/observability/check.py
```

Expected: `observability gate: FAILED` with exactly

```
  - UnattributedOrders: runbook_url names unattributed-order.md, which is not in docs/runbooks
```

and nothing about `bff_orders_unattributed_seconds`: check 4 found the
instrument. Any check-4 line here means the series name is wrong, not the
runbook.

- [ ] **Step 4: Prove check 4 is looking at this rule**

Change the expression's metric to `bff_orders_unattributed_second` (one letter
short), run the gate, and expect, beside Step 3's line:

```
  - UnattributedOrders: reads `bff_orders_unattributed_second`, which no C# instrument declares and EXTERNAL_METRICS does not list. A loaded rule with no signal is silent, and silence reads as health (§13.6)
```

Restore the name and run again: back to Step 3's single line. A gate observed
red on the thing it guards is this repository's rule before it is trusted.

No commit yet: the tree fails a gate until Task 3.

---

### Task 2: The runbook

**Files:**
- Create: `docs/runbooks/unattributed-order.md`

- [ ] **Step 1: Read where PR-4 put the rebuild procedure**

```bash
ls tools/bff-replay/README.md docs/runbooks/
grep -rln 'bff-replay' docs/runbooks tools 2>/dev/null
```

Expected: `tools/bff-replay/README.md` exists and no runbook under
`docs/runbooks/` names the tool. The runbook below links that README. If
PR-4 placed the procedure elsewhere, link the path it chose instead, in both
places Step 2 names it; if PR-4 put it under `docs/runbooks/`, `check.py`
would already be failing on `main`, which is a defect in PR-4 to report rather
than to absorb here.

- [ ] **Step 2: Write the runbook**

````markdown
# Runbook — an order with no owner

| | |
|---|---|
| Alert | `UnattributedOrders`, in `deploy/observability/alerts/platform-alerts.yaml` |
| Condition | `max(bff.orders.unattributed)` > 15 minutes |
| Signal | `ProjectionMetrics`, `src/BFF/Web.Bff/Observability` ([§13.6](../backend-architecture/13-observability.md)) |
| Owner | The team that owns `Web.Bff` ([§13.8](../backend-architecture/13-observability.md)) |

## What it means

The BFF's order projection
([ADR-051](../backend-architecture/adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md))
has a row for an order it has heard about from Payments or Shipping, and no
event from Ordering has told it whose order it is. A row with no owner is
returned by neither order route
([§10.7](../backend-architecture/10-api-gateway.md)), so **one buyer's order
history is missing an order**, and that is the whole of what anyone sees: no
error, no 5xx and no lag goes with it, which is why this gauge exists.

## What is not affected

- **The order itself.** Ordering holds it, the saga runs it, and a cancel goes
  to Ordering rather than through this table.
- **Every other order**, this buyer's included.
- **Checkout.** The quote reads Catalog over §9.7's hop and never this table.

**Nobody is shown somebody else's order.** That is the failure §10.7 refuses,
and an unowned row waiting here is the price of refusing it.

## The lookalike: an Ordering event that is late, not lost

§9.4 orders nothing between consumers, so a `PaymentAuthorised` or a
`ShipmentDispatched` reaching `bff-order-events` before its `OrderPlaced` is
ordinary, and creates exactly the row this gauge measures. It resolves when
the Ordering event lands, inside §13.7's event end-to-end target on a healthy
platform.

**Fifteen minutes is past every cause that resolves itself, and past every
cause that raises its own alert first.** An Ordering broker-lane stall pages
under [`outbox-broker.md`](outbox-broker.md) at its own threshold; a backlog
on `bff-order-events`, or events reaching the BFF late, tickets under
[`queue-backlog.md`](queue-backlog.md) inside its ten-minute window; an
Ordering event the BFF failed on every attempt lands in
`bff-order-events_error` and pages under [`error-queue.md`](error-queue.md).
**If one of those is firing, work it first**: this alert is its symptom here,
and it clears by itself once the event is delivered. What is left when this
fires alone is the silent case — the Ordering event is not coming.

**An empty panel is not a zero.** The gauge contains a failed database read
into an absent series (§13.6's containment callout), so a flat-empty panel
and a quiet alert can mean the read is failing. The BFF logs
`Unattributed-order gauge read failed` when it is.

```promql
max by (service_name) (bff_orders_unattributed_seconds)
```

`max`, never `sum`: the gauge reads the database, so every replica exports the
same number, and the rule, the panel and this file deduplicate alike.

## Find the rows

Against the BFF's database, `Bff`:

```sql
SELECT
    OrderId,
    FirstSeenAt,
    AsOf,
    AuthorisedAt,
    RefundedAt,
    DispatchedAt,
    DeliveredAt,
    TrackingNumber
FROM bff.Orders
WHERE CustomerId IS NULL
ORDER BY FirstSeenAt;
```

`FirstSeenAt` is the BFF's clock when the row was created, which is what the
gauge measures from. The non-null columns say which events have arrived;
none of them can say whose order it is.

## Where the Ordering event is

Take the oldest `OrderId` and ask these in order. Each answer sends you to one
place.

**1. Does Ordering have the order?** Against Ordering's database:

```sql
SELECT Id, CustomerId, Status
FROM ordering.Orders
WHERE Id = @OrderId;
```

**No row** means a payment or shipment event names an order Ordering never
had: a publisher defect in Payments or Shipping, or traffic from another
environment on a shared broker. Nothing here repairs that. Record the order
id and file it against the publisher; then delete the row (below).

**2. Did Ordering stage its event, and did it leave?**

```sql
SELECT
    MessageId,
    MessageType,
    OccurredAt,
    ProcessedAt,
    Attempts,
    LastError = LEFT(LastError, 500)
FROM ordering.OutboxMessages
WHERE Lane = 'Broker'
    AND MessageType IN (
        'Common.Contracts.Ordering.V1.OrderPlaced',
        'Common.Contracts.Ordering.V1.OrderConfirmed',
        'Common.Contracts.Ordering.V1.OrderCancelled')
    AND TRY_CONVERT(uniqueidentifier, JSON_VALUE(Payload, '$.OrderId')) = @OrderId
ORDER BY OccurredAt;
```

`MessageType` is the contract's full name and the payload keeps its property
names as declared, which is why the filter reads `$.OrderId`.

- **`ProcessedAt` null** — the event is still in Ordering's outbox: work
  [`outbox-broker.md`](outbox-broker.md), or
  [`outbox-abandoned.md`](outbox-abandoned.md) when `Attempts` is at the cap.
- **Processed** — it left Ordering. Go to 3 with its `MessageId`.
- **No row, and the order exists** — either retention purged a processed row
  older than `RetentionPolicy.OutboxWindow`, and you are at 4 with nothing to
  replay, or Ordering never staged it, which is a defect in its mapper to
  file.

**3. Did it reach the BFF?** Against `Bff`:

```sql
SELECT MessageId, Endpoint, HandledAt
FROM bff.InboxMessages
WHERE MessageId = @MessageId;
```

- **A row** — the BFF handled the event and the row should have its owner. A
  handled Ordering event that left the row unowned is a defect in the BFF's
  projection; keep the `MessageId` and the row, and file it.
- **No row, and the message is in `bff-order-events_error`** — work
  [`error-queue.md`](error-queue.md): read the fault, fix it, replay. The
  owner arrives with the replay.

  ```bash
  kubectl -n <ns> exec deploy/rabbitmq -- \
    rabbitmqctl list_queues name messages | grep bff-order-events
  ```

- **No row and nothing in `_error`** — the event was published while
  `bff-order-events` was not there to receive it: before the BFF's consumers
  were first deployed to this environment, or during a window the queue was
  deleted. The broker drops what no queue is bound for (§10.7). Go to 4.

**4. Repair from Ordering's outbox, or let the row go.**

- **The outbox row from 2 still exists** — send it to the BFF's queue alone
  with the rebuild tool's repair run, which is the run without `--reset`:
  [`tools/bff-replay/README.md`](../../tools/bff-replay/README.md). **Never
  republish it to the exchange**, which would deliver it a second time to
  every other consumer of `OrderPlaced`.
- **It is gone** — nothing the platform still holds can attribute this order
  (ADR-051; §10.7's last paragraph). The row would stay invisible for ever and
  keep this alert firing. Delete it.

## Delete a row nothing will attribute

Safe, for three reasons: no route returns the row; it holds no lines, because
lines arrive only with an Ordering event and that event brings the owner too;
and if an Ordering event for it ever does arrive, the projection inserts the
missing row and the order reappears whole.

```sql
DELETE FROM bff.Orders
WHERE OrderId = @OrderId
    AND CustomerId IS NULL;
```

The `CustomerId IS NULL` guard makes the statement refuse an order that was
attributed while you were reading. Record the order id and the answer from 1
to 4 in the incident.

**After a rebuild with `--reset`, expect a few of these.** An order whose
payment or shipment events are inside the outbox window and whose `OrderPlaced`
is not is the window's edge, not a fault: the tool prints the oldest instant it
reached per publisher, and every unowned row older than Ordering's is one to
delete.

## How to close it

The alert clears when the oldest unowned row is attributed or deleted. Run the
find query again first: it is what says the rows are gone. Then read the gauge,
which should be back to zero or to a few seconds within a minute.

**A gauge that falls when traffic stops is not a recovery.** No payment or
shipment events means no new unowned rows and no older ones resolved, and an
old row still there keeps the gauge where it was; the find query is the
answer, not the graph.
````

- [ ] **Step 3: Check the runbook's links resolve**

```bash
py -3.12 - <<'EOF'
import pathlib, re
page = pathlib.Path("docs/runbooks/unattributed-order.md")
for target in re.findall(r"\]\(([^)#]+)", page.read_text(encoding="utf-8")):
    path = (page.parent / target).resolve()
    print(("ok      " if path.exists() else "MISSING ") + target)
EOF
```

Expected: every line `ok`. A `MISSING` against the tool's README is Task 2's
Step 1 unfinished.

- [ ] **Step 4: Run the gate and read the failure**

```bash
py -3.12 deploy/observability/check.py
```

Expected: `observability gate: FAILED` with exactly the two check-9 lines

```
  - docs/runbooks/unattributed-order.md: §13.9's table does not name it. The table is the chapter's inventory of procedures, and a runbook missing from it is one no reader of §13.9 knows exists
  - docs/runbooks/unattributed-order.md: no alert row in §13.6 names it. Checks 1 and 2 pair the RULE FILES with the runbooks; this is the chapter those files were written from, and it disagrees
```

Checks 1 and 2 now pass: the rule names a runbook that exists, and the runbook
is named by a rule.

---

### Task 3: §13.6's row, §13.9's row, and the runbooks index

**Files:**
- Modify: `docs/backend-architecture/13-observability.md`
- Modify: `docs/runbooks/README.md`

- [ ] **Step 1: §13.6 — the alert's row, after *Delivery lag***

In §13.6's first table, insert this row immediately after the row whose
Runbook cell is the second `queue-backlog.md` (the *Delivery lag* row) and
before *Saga age*:

```markdown
| Unattributed order | `bff.orders.unattributed` above 15 min | An order the BFF's projection holds payment or shipment facts for and no buyer, so [§10.7](10-api-gateway.md) returns it to nobody: one buyer's history is missing an order, with no error, no 5xx and no lag anywhere ([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)). **Fifteen minutes is past every cause that resolves itself or raises its own row first**: a payment event beating `OrderPlaced` resolves inside §13.7's event end-to-end target, and an Ordering broker-lane stall, a backlog on the BFF's queue and a message in its `_error` queue each fire above inside that time. What is left is the silent case — an Ordering event published before the queue existed, or older than a rebuild reached. A ticket: the order exists in Ordering, and checkout is untouched | `unattributed-order.md` |
```

- [ ] **Step 2: §13.9 — the runbook's row, after `queue-backlog.md`'s**

In §13.9's table, insert after the `docs/runbooks/queue-backlog.md` row:

```markdown
| `docs/runbooks/unattributed-order.md` | An order the BFF holds no buyer for: telling a late Ordering event from a lost one, Ordering's outbox and the BFF's error queue, repairing by the rebuild tool, and deleting a row nothing will attribute |
```

Nothing else in chapter 13 moves. §13.8's table already gives "projection lag
… consumer failures" to the service team, which is the rule's `owner`; the
chapter counts no alerts and no runbooks, so no total goes stale; and §13.9's
"Every row above landed with PR-24" sentence is about the rows it was written
beside and is left, a stale restatement met in passing
(`docs/change-locality.md` §2).

- [ ] **Step 3: The runbooks index**

In `docs/runbooks/README.md`'s table, insert after the `queue-backlog.md` row:

```markdown
| [`unattributed-order.md`](unattributed-order.md) | `UnattributedOrders` | yes |
```

- [ ] **Step 4: Run the gate — green**

```bash
py -3.12 deploy/observability/check.py
```

Expected: `observability gate: OK`.

- [ ] **Step 5: Prove check 9 is reading the new row**

Delete the backticks around `unattributed-order.md` in §13.6's new row's
Runbook cell, run the gate, and expect

```
  - docs/runbooks/unattributed-order.md: no alert row in §13.6 names it. …
```

Restore them, run again: `observability gate: OK`.

- [ ] **Step 6: Commit the rule, the runbook and the rows together**

They land as one commit because the gate fails every subset of them.

```bash
git add deploy/observability/alerts/platform-alerts.yaml docs/runbooks/unattributed-order.md \
  docs/runbooks/README.md docs/backend-architecture/13-observability.md
git commit -F - <<'EOF'
feat(observability): UnattributedOrders over bff.orders.unattributed, and its runbook

ADR-051's projection has one failure no existing signal sees: an order
the BFF holds payment or shipment facts for and no buyer, which §10.7
returns to nobody. A buyer's history is then missing an order with no
error, no 5xx and no lag. PR-2 published the gauge; this loads the rule.

An age, so no `for`, by the outbox lanes' argument in the same file;
`max by (service_name)`, because the gauge reads the database and every
replica exports it. Fifteen minutes, because every cause that resolves
itself does so inside §13.7's end-to-end target, and every cause that
does not — an Ordering broker-lane stall, a backlog on the BFF's queue,
a poisoned message — raises its own alert inside that time; what fires
here alone is the silent case. A ticket, because the order and its money
are untouched.

check.py's check 9 fails a runbook neither of chapter 13's tables names,
so §13.6 gains the alert's row and §13.9 the runbook's; the spec said
§13.6 would not move, and the gate is what says otherwise.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qmua5TeJ385zzCX7sVFk6E
EOF
```

---

### Task 4: The panel

**Files:**
- Modify: `deploy/observability/dashboards/outbox.json`

**Why this dashboard.** `outbox.json` is the messaging dashboard: its last
row already holds delivery lag and own-events read-model staleness, and an
order the projection cannot attribute is the next question about a read model
fed by events. `golden-signals.json` is requests — rate, errors, duration —
and this gauge is none of those.

**Why no `$service` filter.** The dashboard's `service` variable is
`label_values(outbox_pending_count, service_name)`, which the BFF never
exports because it has no outbox, so a filtered panel would draw nothing
whatever the gauge read. Only the BFF exports this series, so the unfiltered
expression already selects exactly one service, and it does not depend on how
the variable is later widened.

- [ ] **Step 1: Add a row and the panel after panel 10**

Append these two objects to `panels`, after the `"id": 10` object (mind the
comma that object now needs):

```json
    {
      "id": 11,
      "type": "row",
      "title": "Projection — an order nobody can see",
      "gridPos": { "h": 1, "w": 24, "x": 0, "y": 35 }
    },
    {
      "id": 12,
      "type": "timeseries",
      "title": "Oldest order with no owner (alert: 15 min)",
      "description": "bff.orders.unattributed (ADR-051): how long the oldest row in the BFF's order projection has waited for an Ordering event to name its buyer. §10.7 returns such a row to nobody, so above zero for long means a buyer's history is missing an order. Seconds is ordinary — a payment or shipment event beat OrderPlaced. Empty is not zero: a failed read is an absent series. Unfiltered by the service variable, which reads an outbox series the BFF does not export. Runbook: docs/runbooks/unattributed-order.md.",
      "datasource": { "type": "prometheus", "uid": "${datasource}" },
      "gridPos": { "h": 8, "w": 24, "x": 0, "y": 36 },
      "fieldConfig": {
        "defaults": {
          "unit": "s",
          "thresholds": {
            "mode": "absolute",
            "steps": [
              { "color": "green", "value": null },
              { "color": "red", "value": 900 }
            ]
          }
        },
        "overrides": []
      },
      "targets": [
        {
          "expr": "max by (service_name) (bff_orders_unattributed_seconds)",
          "legendFormat": "{{service_name}}"
        }
      ]
    }
```

- [ ] **Step 2: The file still parses, and the ids and positions are unique**

```bash
py -3.12 - <<'EOF'
import json
panels = json.load(open("deploy/observability/dashboards/outbox.json", encoding="utf-8"))["panels"]
ids = [p["id"] for p in panels]
assert len(ids) == len(set(ids)), ids
print("ok", ids[-2:], panels[-1]["gridPos"])
EOF
```

Expected: `ok [11, 12] {'h': 8, 'w': 24, 'x': 0, 'y': 36}`.

- [ ] **Step 3: Run the gate, then prove check 6 reads the panel**

```bash
py -3.12 deploy/observability/check.py
```

Expected: `observability gate: OK`. Then change the panel's metric to
`bff_orders_unattributed_second`, run again, and expect

```
  - outbox.json: a panel reads `bff_orders_unattributed_second`, which no C# instrument declares and EXTERNAL_METRICS does not list
```

Restore it, run again: `OK`.

- [ ] **Step 4: Commit**

```bash
git add deploy/observability/dashboards/outbox.json
git commit -F - <<'EOF'
feat(observability): the oldest unattributed order on the messaging dashboard

A panel for bff.orders.unattributed under a row of its own on
outbox.json, beside delivery lag and own-events staleness: an order the
projection cannot attribute is the next question about a read model fed
by events. Unfiltered by the dashboard's service variable, which reads
outbox_pending_count — a series the BFF never exports — so a filtered
panel would draw nothing; only the BFF exports this gauge. The red
threshold is the rule's, and the description says that an empty panel
is an absent series rather than a zero.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qmua5TeJ385zzCX7sVFk6E
EOF
```

---

### Task 5: Verification and the PR

- [ ] **Step 1: Every gate this tree is under**

```bash
py -3.12 deploy/observability/check.py
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: `observability gate: OK`; the comment gate passes — the one new
comment block is five lines, names nothing it may not and stresses nothing;
the secret scan reports no unexplained finding. The comment gate judges HEAD,
so it runs after both commits.

- [ ] **Step 2: The chapter audit**

Run `/validate-blueprint`: chapter 13 was edited, and spec section 12's last
line requires it on every PR that edits a chapter. Fix what it finds in a
commit of its own, then run Step 1 again.

- [ ] **Step 3: The locality gate over the drafted body**

Draft the PR body with `/pr`'s house form into `artifacts/pr-body.md`, with
`| Class | D |` and the touch-set row above, then judge it with `main`'s copy of
the gate:

```bash
mkdir -p artifacts/locality
git show origin/main:.github/locality-gate/locality_gate.py > artifacts/locality/locality_gate.py
git show origin/main:.github/locality-gate/classes.yml > artifacts/locality/classes.yml
py -3.12 - <<'EOF' > artifacts/locality/input.json
import json, subprocess
names = subprocess.run(
    ["git", "diff", "--name-only", "origin/main...HEAD"],
    capture_output=True, encoding="utf-8", check=True).stdout.split()
body = open("artifacts/pr-body.md", encoding="utf-8").read()
print(json.dumps({"number": 0, "body": body, "changedFiles": len(names),
                  "files": [{"filename": n, "previous_filename": None} for n in names]}))
EOF
py -3.12 artifacts/locality/locality_gate.py --map artifacts/locality/classes.yml < artifacts/locality/input.json
```

Expected: the gate passes; the five paths are the diff, and each is inside
Class D's tree and the body's row.

- [ ] **Step 4: Open the PR**

`/pr`. The body names the class and the touch set, says `/validate-blueprint`
ran, says no check was skipped, and closes the issue this pull request was
filed under with a bare `Closes #n` in the commit body or the PR body as
`/pr` directs. If this is the last of the five, #425 is closed by it too — the
spec's section 4 says #425 closes with the last.

---

## Self-review

- **Spec section 1, *Monitoring*.** The gauge's rule and runbook are this PR's
  (Tasks 1 and 2); `projection.lag` is not written to and no rule reads it for
  the BFF.
- **Spec section 4, row 5.** The rule over `bff.orders.unattributed` (Task 1),
  the runbook it maps to (Task 2), the dashboard panel (Task 4). Class D; the
  touch set is five paths under `deploy/` and `docs/`. The order paragraph's
  "PR-5 needs the gauge PR-2 adds" is Task 1, Step 1's guard.
- **Spec section 10.** Threshold in minutes, argued from the ordinary
  interleaving and from the alerts that fire first, without restating §13.7's
  target or any other rule's threshold (Task 1's comment, §13.6's row, the
  runbook's lookalike section). The runbook looks for Ordering's outbox (step
  2), the event's `_error` queue (step 3), and an `OrderPlaced` older than the
  queue (steps 3 and 4) — all three the spec names — and adds the two it does
  not: an order Ordering never had, and the deletion that closes a row nothing
  will attribute.
- **Spec section 12.** §13.6 moves where the spec said it would not, because
  `check.py`'s check 9 requires the row; §13.9 moves with it; reported as a
  spec defect. No count is written anywhere: the chapter's tables gain rows,
  and no sentence totals them.
- **Names consumed** match PR-1's and PR-2's interface sections: `bff.Orders`'
  columns, `bff.InboxMessages`' three, `bff-order-events`, the meter and the
  gauge with unit `s`, and the gauge-failure log message. PR-4's tool path is
  read from the merged tree in Task 2, Step 1, not assumed.
- **Gates observed red before trusted**: check 4 (Task 1, Step 4), checks 1
  and 2 then 9 in sequence (Tasks 1 to 3), check 9 on the new row (Task 3,
  Step 5), check 6 on the panel (Task 4, Step 3).
- **Style.** Prose at 80 columns, British spelling; the YAML block is five
  lines with no issue, PR, reviewer or emphasis; no credential printed; no
  `docs/superpowers/` file edited.
