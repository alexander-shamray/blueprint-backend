# The game day

A runbook nobody has executed is fiction. This directory **causes** a runbook's
alert on the Compose stack, checks that the alert fires, and runs the
runbook's first step as written. #430 is the specification; this README owns
what the harness does and what a pass proves.

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
py -3.12 tools/game-day/game_day.py outbox-broker      # or projection-lag, outbox-abandoned
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
| `harness.py` | Compose control (`stop`, `start`, `pause`, `exec_sql`, `exec_redis`, `logs`), a token and an order, alert state, `wait_until` and `Deadline` |
| `game_day.py` | The runner: quiet start, cause, wait, first step, restore, in that order |
| `scenarios/<runbook>.py` | One runbook, named for it with `-` as `_` |
| `runbook_coverage.py` | `NOT_ON_COMPOSE` and `OWED`, and the rules that hold every runbook to a script or a reason |
| `test_harness.py`, `test_coverage.py` | The suite, run in CI by `.github/workflows/game-day.yml` |

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

## Waits are predicates with deadlines

`wait_until` polls a predicate and fails naming the last thing it saw; there is
no sleep that stands in for a condition. A `Deadline` is a sum written out, as
`tests/Platform.IntegrationTests/Journey/Deadlines.cs` writes its own: the
rule's threshold (an age gauge needs that many seconds to cross it), plus its
`for:`, plus the export interval (60 seconds, the OpenTelemetry SDK's default,
which the suite fails on the day anything under `src/`, `deploy/compose/` or `deploy/helm/` sets it),
plus the evaluation interval (60 seconds). The bundled Grafana image's Prometheus
configuration sets no `evaluation_interval`, which was read from the running
container, so the default applies. The first runs, on 2026-10-10, fired
`OutboxAbandonedRows` after 75 seconds of a 120-second deadline,
`OutboxLocalLaneStalled` after 128 of 150, and `OutboxBrokerLaneStalled` after
209 and 226 of 240. The last is close to its ceiling, which is what a sum of
ceilings looks like when the cause lands on a bad phase of both intervals.

## Alert state

Prometheus's port is not published, so alerts are read through Grafana's
datasource proxy, `/api/datasources/proxy/uid/prometheus/api/v1/alerts`, the
route `compose.yml`'s smoke already uses. A rule Prometheus has not loaded is
also quiet, so the runner asks `/api/v1/rules` first and refuses to start
against an alert that is already firing.

## Findings the first run made

Each is a defect in a runbook or a rule, not in this tool, and each has its
own issue; the scripts keep reporting them until they are repaired.

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
