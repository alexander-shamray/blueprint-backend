# The game day

A runbook nobody has executed is fiction. This directory **causes** a runbook's
alert on the Compose stack, checks that the alert fires, and runs the
runbook's first step as written. #430 is the specification; this README owns
what the harness does and what a pass proves.

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
py -3.12 tools/game-day/game_day.py outbox-broker      # or any runbook --list shows a script for
py -3.12 tools/game-day/game_day.py --list             # a script, or why the runbook has none
cd tools/game-day && py -3.12 -m unittest              # the suite; needs no Docker
```

Stdlib Python, like `deploy/observability/check.py`. Exit 0 means the alert
was quiet before the cause, fired within its deadline, the first step worked,
and the restore settled. Anything else prints a `FINDING:` line, and **a finding
is the product**: each one is a defect in a rule, a step or a metric, and gets
its own issue. This tool records them and repairs none.

## What a pass proves, and what it does not

It proves one thing about a runbook on one host per service: a person who
follows the runbook's first step on this stack sees what the runbook says they
will. It is not a chaos suite and says nothing about a cluster. A step written
for a cluster (`kubectl`, `helm`) is run as its Compose equivalent, and the
script's header names the translation; a translated step is a finding to list,
not a failure.

## The pieces

| File | Owns |
|---|---|
| `harness.py` | Compose control (`stop`, `start`, `restart`, `pause`, `exec_sql`, `exec_redis`, `logs`), a token and an order, alert state and PromQL (`Alerts`), Loki (`Logs`), a service account's Keycloak grants (`Realm`, `Grant`), the request generator (`Traffic`), `wait_until` and `Deadline` |
| `game_day.py` | The runner: quiet start, cause, wait, first step, restore, in that order, then the scenario's `NEXT` if it has one |
| `scenarios/<runbook>.py` | One runbook, named for it with `-` as `_` |
| `runbook_coverage.py` | `NOT_ON_COMPOSE` and `OWED`, and the rules that hold every runbook to a script or a reason |
| `test_harness.py`, `test_refused_reads.py`, `test_sustained_traffic.py`, `test_coverage.py` | The suite, run in CI by `.github/workflows/game-day.yml` |

## The shape of a script

Every script is a module that follows this, and the coverage test holds it:

- A **header** with `Cause:`, `First step:` and `Restore:`, naming the alert.
  A cause forced through SQL says `Forced through SQL` and gives the organic
  route and its wall-clock time; a first step written for a cluster says
  `Translated`.
- `RUNBOOK`, `ALERT` and `DEADLINE`, and the calls `cause(world)`,
  `first_step(world) -> (ok, detail)`, `restore(world)` and
  `settled(world) -> (ok, observed)`.

`restore` runs whatever the cause did, including when the cause raised
half-way, and must be safe to run twice. A restore that does not settle is a
finding, because the next scenario would start poisoned by it.

A rule over a window (`increase(...[30m]) > 0`) stays true until the last
sample leaves it, which the cause's sum does not bound, so such a script names
`SETTLE`, a `Deadline` as long as the window, and the runner waits that long
for the restore instead. The coverage test holds `SETTLE` to the rule's window.
A restore that takes a grant away from the stack gives it back and reads it
back (`Grant.give_back`), because a restore assumed is the next scenario's
poison.

A runbook two rules share (`error-rate.md`) is one module with the second rule's
phase as `NEXT`, an object with the same calls, and the coverage test holds it
to the same rules. The runner runs it only after the first phase's restore
settled, because a stack that did not settle would make it a finding about the
first.

## The traffic loop

`ErrorRateGateway`, `ErrorRateService` and `Latency` are a ratio and a quantile
over requests served, so with none they have no series and never fire. A script
that needs them starts `world.traffic` in its `cause`, after which it waits for
the loop's requests to reach Prometheus (the quiet start of a rule that reads
requests), and the runner stops the loop after the settle and not before it:
an empty window is a quiet rule whether or not the cause went. It is a
fixture, not a load test; `deploy/observability/slo/slo.js` is §13.7's.

Each tick sends one request, alternating an anonymous `GET` of the catalog and
an authenticated cancel of an order id that does not exist (a 404 that reads
Ordering's database and writes nothing). **The rate is one request every 3
seconds, the lowest that keeps the widest window populated**: `Latency`'s p99
over 10 minutes reads its maximum when fewer than 100 observations stand under
it, 100 in 600 seconds is one for each route every 6 seconds, and two routes
sent alternately make 3. It sends 20 a minute against the gateway's 100 per
address and 300 per subject (§10.3), so the loop is never the 429s.
`ErrorRate`'s 5-minute window holds 50 for a service, and the ratio crosses 1%
at one failure in 50. A request a database holds runs on its own thread,
bounded at 40 out, and the client waits 45 seconds so that a request held for a
SQL command's 30 ends as the server's answer and not as the loop hanging up.

## Waits are predicates with deadlines

`wait_until` polls a predicate and fails naming the last thing it saw; there is
no sleep that stands in for a condition. A `Deadline` is a sum written out, as
`tests/Platform.IntegrationTests/Journey/Deadlines.cs` writes its own: the
rule's threshold (an age gauge needs that many seconds to cross it), plus its
`for:`, plus the export interval (60 seconds, the OpenTelemetry SDK's default,
which the suite fails on the day anything under `src/`, `deploy/compose/` or `deploy/helm/` sets it),
plus the evaluation interval (60 seconds), plus the same interval again for
reporting. The bundled Grafana image's Prometheus
configuration sets no `evaluation_interval`, which was read from the running
container, so the default applies. The first runs, on 2026-10-10, fired
`OutboxAbandonedRows` after 75 seconds of a 120-second deadline,
`OutboxLocalLaneStalled` after 128 of 150, and `OutboxBrokerLaneStalled` after
209 and 226 of 240. The last is close to its ceiling, which is what a sum of
ceilings looks like when the cause lands on a bad phase of both intervals.

**Reporting is a second evaluation.** Prometheus shows an alert one evaluation
after the one whose expression first read true: the rule's entry in
`/api/v1/rules` has no alert at that evaluation, and the next one lists it
`firing` with an `activeAt` back-dated to the earlier. `UnscannedShipments`,
a count that reads above zero the moment it is exported, fired 169, 172, 176
and 181 seconds after its row was planted, where the export and one evaluation
allow 125. The three terms above could not hold it, so `REPORT_LAG_SECONDS` is
the fourth and every deadline carries it; the runs above fit under it too.

## Alert state

Prometheus's port is not published, so alerts are read through Grafana's
datasource proxy, `/api/datasources/proxy/uid/prometheus/api/v1/alerts`, the
route `compose.yml`'s smoke already uses. A rule Prometheus has not loaded is
also quiet, so the runner asks `/api/v1/rules` first and refuses to start
against an alert that is already firing.

## Findings the runs made

Each is a defect in a runbook or a rule, not in this tool, and each has its
own issue; a script keeps reporting one it can reach until it is repaired, and
names in its header one it has to work around.

- **A container's stdout is empty.** The hosts log through OpenTelemetry alone
  (§13.4), so the `kubectl logs … | grep` that opens `outbox-broker.md` and
  `projection-lag.md` finds nothing on any deployment. The scripts read Loki
  instead and say so in their headers.
- **A stopped broker does not produce the runbook's second branch.** MassTransit
  retries inside the publish, so the dispatcher never logs `Outbox message …
  failed, attempt N of 10`; it logs nothing, and the runbook reads that silence
  as a dispatcher that is not running. `outbox-broker.md`'s first step fails
  on this.
- **`sqlcmd` needs `-I`.** The runbooks' SQL is written for a client with
  `QUOTED_IDENTIFIER` on; `sqlcmd` leaves it off, and the outbox's filtered
  indexes refuse a write without it. The harness passes `-I`.

- **A runbook's "with the refusal as its exception" is not in the line.**
  `address-refused.md` and `contact-refused.md` tell their four causes apart by
  the exception on the `PassFailed` and `ContactRefused` lines. Loki holds the
  line without it: the exception is the entry's `exception_message` metadata,
  so a reader who greps the line for the wording finds nothing. The scripts
  filter on that field (`Logs.search(..., exception=)`) and say so in their
  headers.
- **`DeliveryLagHigh` cannot see a consumer's first export.** A restarted
  consumer is a new series, and `rate` counts nothing for a series' first
  sample, which is where the deliveries it was late with land. The runbook's
  lookalike, a consumer in a crash loop, therefore raises no lag at all while
  its backlog grows; `queue_backlog.py` pauses the consumer for that reason
  and not with the row's `stop`.

## The scripts

The runtime is the wall clock of one run on the Compose stack, from the quiet
start to the restore settling, measured on 2026-10-10; the first three were
not timed whole, and the deadline paragraph above has when they fired.

| Runbook | Alert | Cause | Runtime |
|---|---|---|---|
| `outbox-broker` | `OutboxBrokerLaneStalled` | `stop rabbitmq` | not timed |
| `projection-lag` | `OutboxLocalLaneStalled` | a table renamed through SQL | not timed |
| `outbox-abandoned` | `OutboxAbandonedRows` | a row planted at the ceiling | not timed |
| `queue-backlog` (lag half) | `DeliveryLagHigh` | `web-bff` paused for 20 seconds in each round, for 12 minutes, to outlast the `for:` | 23m16s |
| `address-refused` | `AddressReadRefused` | `orders:delivery-address` taken from `shipping-worker`'s service account, the worker restarted | 34m45s |
| `contact-refused` | `ContactReadRefused` | `view-users` taken from `notifications-worker`'s, the stored contacts dropped | 32m40s |
| `unscanned-shipment` | `UnscannedShipments` | a four-day-old Booked shipment planted through SQL | 4m08s |
| `unattributed-order` | `UnattributedOrders` | an unowned payment fact planted through SQL, held 900 seconds | 18m40s |
| `erasure-overdue` | `ErasureRequestsOverdue` | an Open request due a day ago planted through SQL, which Privacy's sweep marks | not timed |
| `error-rate` | `ErrorRateGateway`, then `ErrorRateService` | `stop ordering-api` behind the gateway; then `stop sql` under the services | 27m36s |
| `latency` | `Latency` | `pause sql` under the loop, held past the 10-minute `for:` | 24m18s |
| `outbox-growth` | `OutboxGrowth` | `stop rabbitmq`, 1100 Broker rows planted through SQL and 20 more every 30 seconds | 14m34s |

The three runs above were measured on 2026-10-10, each alone on a stack that
had just come up. `error-rate` is two runs in one, and its two phases fired
after 445 and 412 seconds of a 480-second deadline. `latency` fired after 787
of 840, with nine services over a second, which is the runbook's *everything
slow together* shape. `outbox-growth` fired after 738 of 780 and asserted
`OutboxBrokerLaneStalled` beside it: with the broker stopped the age gauge is
high too, so the runbook's own first check sends its reader to
`outbox-broker.md`, which is the right answer for this cause and says growth
alone cannot be told from a stall by the count. `stop sql` makes
`ErrorRateGateway` fire as well, because the gateway passes a service's 500
through, and the runbook's advice to work the backend first is that case.

The two Keycloak causes restart the worker because it keeps the token it
fetched until that expires. Their restore gives the grant back, reads the
account back, restarts the worker, and then waits out the rule's 30-minute
window, which is why they take over half an hour.

## The order

`walk-an-order.sh` is the route: a password-grant token for `demo` from
Keycloak, then `POST /api/v1/orders` through the gateway. `harness.Orders` is
that route in Python, and the suite reads the script and fails if its
addresses, client or product stop agreeing.

## The coverage test

Every file in `docs/runbooks/` except `check.py`'s `NOT_A_RUNBOOK` has a
script or a stated reason not to. The test reads the directory and keeps no
list of runbooks, so a new runbook fails it until it is dealt with.

- `NOT_ON_COMPOSE` is permanent and carries its reason.
- `OWED` names the pull request of #430 that delivers the script. A runbook
  two rules share (`check.py`'s `SHARED_RUNBOOKS`) may have a script for one
  rule and stay `OWED` the other's. PR-4 empties `OWED` and deletes it.

## Running it in CI

`.github/workflows/game-day.yml` runs the suite on the pull requests that touch
what it reads. The game day itself is `workflow_dispatch` only, until PR-4
decides from the measured runtimes.

The dispatch job's `timeout-minutes` is 60 and runs its runbooks in turn, so
one dispatch must name runbooks whose runtimes in *The scripts* sum to less:
`address-refused` and `contact-refused` take over half an hour each and go in
dispatches of their own, and `error-rate` (27m36s) with `latency` (24m18s) is
52 minutes, which fits only with nothing else beside it. A job that times out
still runs its teardown.
